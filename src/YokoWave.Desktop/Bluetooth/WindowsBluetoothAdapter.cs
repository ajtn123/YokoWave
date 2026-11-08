#if WINDOWS
using System.Collections.Concurrent;
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
    public Action<string>? Diagnostic { get; init; }

    public async Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct);
        if (adapter is null || !adapter.IsLowEnergySupported) throw new LocalizedOperationException("Error.BluetoothUnavailable");
        var radio = await adapter.GetRadioAsync().AsTask(ct);
        if (radio is not null && radio.State != RadioState.On) throw new LocalizedOperationException("Error.BluetoothOff");
        var found = new ConcurrentDictionary<ulong, BluetoothPeripheral>();
        var candidates = new ConcurrentDictionary<ulong, string>();
        var received = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
        {
            Interlocked.Increment(ref received);
            var name = e.Advertisement.LocalName?.Trim() ?? "";
            if (e.Advertisement.ServiceUuids.Contains(V1Protocol.Service) ||
                name.StartsWith("YYC-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("YCY-", StringComparison.OrdinalIgnoreCase))
                candidates[e.BluetoothAddress] = $"Candidate: name={name}, FF30={e.Advertisement.ServiceUuids.Contains(V1Protocol.Service)}, RSSI={e.RawSignalStrengthInDBm}";
            if (ElectricalDeviceProfile.FromName(name) is not { } profile) return;
            found[e.BluetoothAddress] = new($"ble:{e.BluetoothAddress:X12}", profile.Name, e.RawSignalStrengthInDBm);
        }
        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e)
        {
            if (e.Error == BluetoothError.Success) stopped.TrySetResult();
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
            var completion = await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(8), ct), stopped.Task);
            await completion;
            Diagnostic?.Invoke($"BLE advertisements received: {received}; related candidates: {candidates.Count}");
            foreach (var candidate in candidates.Values) Diagnostic?.Invoke(candidate);
            return found.Values.OrderByDescending(p => p.Signal).ToArray();
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
        if (ElectricalDeviceProfile.FromName(peripheral.Name) is null || !peripheral.Id.StartsWith("ble:", StringComparison.Ordinal) ||
            !ulong.TryParse(peripheral.Id.AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            throw new LocalizedOperationException("Error.BluetoothRescan");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        GattSession? session = null;
        WindowsBluetoothConnection? connection = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token)
                ?? throw new LocalizedOperationException("Error.BluetoothConnect");
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token);
            if (session is not null) session.MaintainConnection = true;
            var services = await device.GetGattServicesForUuidAsync(V1Protocol.Service, BluetoothCacheMode.Uncached).AsTask(token);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                throw new LocalizedOperationException("Error.BluetoothService");
            service = services.Services[0];
            foreach (var unused in services.Services.Skip(1)) unused.Dispose();
            var writes = await service.GetCharacteristicsForUuidAsync(V1Protocol.WriteCharacteristic, BluetoothCacheMode.Uncached).AsTask(token);
            var notifications = await service.GetCharacteristicsForUuidAsync(V1Protocol.NotifyCharacteristic, BluetoothCacheMode.Uncached).AsTask(token);
            var write = writes.Characteristics.FirstOrDefault();
            var notify = notifications.Characteristics.FirstOrDefault();
            if (writes.Status != GattCommunicationStatus.Success || notifications.Status != GattCommunicationStatus.Success ||
                write is null || notify is null || !write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ||
                !notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                throw new LocalizedOperationException("Error.BluetoothService");
            connection = new(device, service, session, write, notify);
            await connection.StartAsync(token);
            return connection;
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            else { if (session is not null) { session.MaintainConnection = false; session.Dispose(); } service?.Dispose(); device?.Dispose(); }
            throw;
        }
    }
}

internal sealed class WindowsBluetoothConnection : IBluetoothConnection
{
    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattSession? _session;
    private readonly GattCharacteristic _write;
    private readonly GattCharacteristic _notify;
    private bool _ready;
    private bool _closed;
    public event Action<byte[]>? NotificationReceived;
    public event Action? Disconnected;

    public WindowsBluetoothConnection(BluetoothLEDevice device, GattDeviceService service, GattSession? session,
        GattCharacteristic write, GattCharacteristic notify)
    {
        _device = device; _service = service; _session = session; _write = write; _notify = notify;
        _device.ConnectionStatusChanged += OnConnectionChanged;
        _notify.ValueChanged += OnValueChanged;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var status = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        if (status != GattCommunicationStatus.Success || _device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            throw new LocalizedOperationException("Error.BluetoothSubscribe");
        _ready = true;
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        if (_closed) return;
        using var reader = DataReader.FromBuffer(e.CharacteristicValue);
        var bytes = new byte[e.CharacteristicValue.Length];
        reader.ReadBytes(bytes);
        NotificationReceived?.Invoke(bytes);
    }

    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (_ready && !_closed && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        { _ready = false; Disconnected?.Invoke(); }
    }

    public async Task WriteAsync(byte[] packet, CancellationToken ct)
    {
        if (_closed || !_ready) throw new LocalizedIOException("Error.BluetoothDisconnected");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var writer = new DataWriter();
        writer.WriteBytes(packet);
        try
        {
            var status = await _write.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(timeout.Token);
            if (status != GattCommunicationStatus.Success)
            {
                var error = new LocalizedIOException("Error.BluetoothWrite");
                error.Data["GattStatus"] = status; throw error;
            }
        }
        catch (ArgumentException error)
        {
            var failure = new LocalizedIOException("Error.BluetoothWrite", error);
            failure.Data["Operation"] = "GattWriteWithoutResponse";
            failure.Data["PacketLength"] = packet.Length;
            throw failure;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        _notify.ValueChanged -= OnValueChanged;
        _device.ConnectionStatusChanged -= OnConnectionChanged;
        try
        {
            if (_device.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(timeout.Token);
            }
        }
        catch { /* Always release native handles, even after a radio loss. */ }
        finally
        {
            if (_session is not null) { _session.MaintainConnection = false; _session.Dispose(); }
            _service.Dispose(); _device.Dispose();
        }
    }
}
#endif
