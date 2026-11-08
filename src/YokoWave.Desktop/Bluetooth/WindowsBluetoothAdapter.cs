#if WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Storage.Streams;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Desktop.Bluetooth;

internal sealed class WindowsBluetoothAdapter : IBluetoothAdapter
{
    public Action<string>? Diagnostic
    { get; init; }
    public async Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct);
        if (adapter is null || !adapter.IsLowEnergySupported)
            throw new LocalizedOperationException("Error.BluetoothUnavailable");
        var radio = await adapter.GetRadioAsync().AsTask(ct);
        if (radio is not null && radio.State != RadioState.On)
            throw new LocalizedOperationException("Error.BluetoothOff");
        var found = new ConcurrentDictionary<ulong, BluetoothPeripheral>();
        var received = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
        {
            Interlocked.Increment(ref received);
            var name = e.Advertisement.LocalName?.Trim() ?? "";
            var services = e.Advertisement.ServiceUuids.ToArray();
            if (BluetoothDeviceProfiles.Identify(name, services) is null)
                return;
            found[e.BluetoothAddress] = new($"ble:{e.BluetoothAddress:X12}", name.Length > 0 ? name : $"BLE {e.BluetoothAddress:X12}", e.RawSignalStrengthInDBm, services);
        }
        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e)
        {
            if (e.Error == BluetoothError.Success)
                stopped.TrySetResult();
            else
            {
                var error = new LocalizedOperationException("Error.BluetoothScan");
                error.Data["BluetoothError"] = e.Error;
                stopped.TrySetException(error);
            }
        }
        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        try
        {
            watcher.Start();
            await await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(8), ct), stopped.Task);
            Diagnostic?.Invoke($"BLE advertisements received: {received}; supported-profile candidates: {found.Count}");
            return found.Values.OrderByDescending(peripheral => peripheral.Signal).ToArray();
        }
        finally
        {
            watcher.Received -= OnReceived;
            watcher.Stopped -= OnStopped;
            watcher.Stop();
        }
    }
    public async Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct)
    {
        var profile = BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services);
        if (profile is null || !peripheral.Id.StartsWith("ble:", StringComparison.Ordinal) ||
            !ulong.TryParse(peripheral.Id.AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            throw new LocalizedOperationException("Error.BluetoothRescan");
        if (!profile.Supported)
            throw new LocalizedOperationException("Error.PawFirmware");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        BluetoothLEDevice? device = null;
        GattSession? session = null;
        List<GattDeviceService> ownedServices = [];
        WindowsBluetoothConnection? connection = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token) ?? throw new LocalizedOperationException("Error.BluetoothConnect");
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token);
            if (session is not null)
                session.MaintainConnection = true;
            var attributes = await new WindowsGattResolver(device, ownedServices).ResolveAsync(profile, token);
            foreach (var (endpoint, characteristic) in attributes)
                Diagnostic?.Invoke($"GATT {profile.Protocol}: service={endpoint.Service}, characteristic={endpoint.Characteristic}, properties={characteristic.CharacteristicProperties}, MTU={session?.MaxPduSize}");
            connection = new(device, ownedServices, session, profile, attributes);
            await connection.StartAsync(token);
            return connection;
        }
        catch (Exception error)
        {
            List<Exception> errors = [];
            void Release(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception cleanupError)
                {
                    errors.Add(cleanupError);
                }
            }
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception cleanupError)
                {
                    errors.Add(cleanupError);
                }
            }
            else
            {
                if (session is not null)
                {
                    Release(() => session.MaintainConnection = false);
                    Release(session.Dispose);
                }
                foreach (var service in ownedServices)
                    Release(service.Dispose);
                if (device is not null)
                    Release(device.Dispose);
            }
            if (errors.Count > 0)
                throw new LocalizedAggregateException("Error.Cleanup", [error, .. errors]);
            throw;
        }
    }
}

internal sealed class WindowsBluetoothConnection : IBluetoothConnection
{
    public int MaximumWriteLength => Math.Max(0, (_session?.MaxPduSize ?? 23) - 3);
    private readonly BluetoothLEDevice _device;
    private readonly IReadOnlyList<GattDeviceService> _services;
    private readonly GattSession? _session;
    private readonly BluetoothDeviceProfile _profile;
    private readonly IReadOnlyDictionary<GattAddress, GattCharacteristic> _attributes;
    private readonly List<(GattCharacteristic Characteristic, Windows.Foundation.TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> Handler)> _notifications = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready, _closed;
    public event Action<byte[]>? NotificationReceived;
    public event Action<GattNotification>? AdditionalNotificationReceived;
    public event Action? Disconnected;
    public WindowsBluetoothConnection(BluetoothLEDevice device, IReadOnlyList<GattDeviceService> services, GattSession? session,
        BluetoothDeviceProfile profile, IReadOnlyDictionary<GattAddress, GattCharacteristic> attributes)
    {
        _device = device;
        _services = services;
        _session = session;
        _profile = profile;
        _attributes = attributes;
        _device.ConnectionStatusChanged += OnConnectionChanged;
    }
    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var endpoint in _profile.Endpoints.Where(endpoint => endpoint.Notify))
        {
            if (!_attributes.TryGetValue(endpoint.Address, out var characteristic) || !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                continue;
            void OnValue(GattCharacteristic sender, GattValueChangedEventArgs args)
            {
                if (_closed)
                    return;
                using var reader = DataReader.FromBuffer(args.CharacteristicValue);
                var bytes = new byte[args.CharacteristicValue.Length];
                reader.ReadBytes(bytes);
                if (endpoint.Address == _profile.Notify)
                    NotificationReceived?.Invoke(bytes);
                else
                    AdditionalNotificationReceived?.Invoke(new(endpoint.Address, bytes));
            }
            characteristic.ValueChanged += OnValue;
            _notifications.Add((characteristic, OnValue));
            var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
            if (result != GattCommunicationStatus.Success && endpoint.Required)
                throw new LocalizedOperationException("Error.BluetoothSubscribe");
        }
        if (_device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            throw new LocalizedOperationException("Error.BluetoothConnect");
        _ready = true;
    }
    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (_ready && !_closed && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _ready = false;
            Disconnected?.Invoke();
        }
    }
    public Task WriteAsync(byte[] packet, CancellationToken ct) => WriteAsync(_profile.Write, packet, ct);
    public async Task WriteAsync(GattAddress address, byte[] packet, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var properties = GattCharacteristicProperties.None;
        ushort mtu = 23;
        try
        {
            CheckReady();
            if (!_attributes.TryGetValue(address, out var characteristic))
                throw new LocalizedOperationException("Error.BluetoothService");
            properties = characteristic.CharacteristicProperties;
            mtu = _session?.MaxPduSize ?? 23;
            var option = properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
            if (packet.Length == 0 || packet.Length > Math.Max(20, mtu - 3))
                throw new LocalizedIOException("Error.BluetoothWrite");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var writer = new DataWriter();
            writer.WriteBytes(packet);
            var buffer = writer.DetachBuffer();
            try
            {
                var result = await characteristic.WriteValueWithResultAsync(buffer, option).AsTask(timeout.Token);
                if (result.Status != GattCommunicationStatus.Success)
                {
                    var error = new LocalizedIOException("Error.BluetoothWrite");
                    error.Data["GattStatus"] = result.Status;
                    error.Data["GattProtocolError"] = result.ProtocolError;
                    throw error;
                }
            }
            finally
            {
                GC.KeepAlive(buffer);
            }
        }
        catch (ArgumentException error)
        {
            var failure = new LocalizedIOException("Error.BluetoothWrite", error);
            failure.Data["Characteristic"] = address.Characteristic;
            failure.Data["PacketLength"] = packet.Length;
            failure.Data["HResult"] = $"0x{error.HResult:X8}";
            failure.Data["CharacteristicProperties"] = properties;
            failure.Data["MTU"] = mtu;
            Trace.TraceError("GATT write failed: UUID={0}, HRESULT=0x{1:X8}, bytes={2}, properties={3}, MTU={4}", address.Characteristic, error.HResult, packet.Length, properties, mtu);
            throw failure;
        }
        finally
        {
            _gate.Release();
        }
    }
    public async Task<byte[]?> ReadAsync(GattAddress address, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            CheckReady();
            if (!_attributes.TryGetValue(address, out var characteristic))
                return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
            if (result.Status != GattCommunicationStatus.Success)
            {
                if (_profile.Endpoints.Any(endpoint => endpoint.Address == address && endpoint.Required))
                    throw new LocalizedIOException("Error.BluetoothState");
                return null;
            }
            using var reader = DataReader.FromBuffer(result.Value);
            var bytes = new byte[result.Value.Length];
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            _gate.Release();
        }
    }
    private void CheckReady()
    {
        if (_closed || !_ready || _device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            throw new LocalizedIOException("Error.BluetoothDisconnected");
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_closed)
                return;
            _closed = true;
            _ready = false;
            _device.ConnectionStatusChanged -= OnConnectionChanged;
            foreach (var (characteristic, handler) in _notifications)
            {
                characteristic.ValueChanged -= handler;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    if (_device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                        await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(timeout.Token);
                }
                catch { /* Releasing a lost link must still close every native handle. */ }
            }
            List<Exception> errors = [];
            void Release(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            if (_session is not null)
            {
                Release(() => _session.MaintainConnection = false);
                Release(_session.Dispose);
            }
            foreach (var service in _services)
                Release(service.Dispose);
            Release(_device.Dispose);
            if (errors.Count > 0)
                throw new LocalizedAggregateException("Error.Cleanup", errors);
        }
        finally
        {
            _gate.Release();
        }
    }
}
#endif
