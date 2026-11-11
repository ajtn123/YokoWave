#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Desktop.Bluetooth;

internal sealed class WindowsBluetoothConnection : IBluetoothConnection
{
    private readonly BluetoothLEDevice _device;
    private readonly IReadOnlyList<GattDeviceService> _services;
    private readonly GattSession? _session;
    private readonly BluetoothDeviceProfile _profile;
    private readonly IReadOnlyDictionary<GattAddress, GattCharacteristic> _attributes;
    private readonly Action<string>? _diagnostic;
    private readonly List<(GattCharacteristic Characteristic, Windows.Foundation.TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> Handler)> _notifications = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;
    private volatile bool _closed;
    public IReadOnlyList<GattAttribute> Attributes => _attributes.Select(pair => new GattAttribute(pair.Key,
        pair.Value.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read),
        pair.Value.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write),
        pair.Value.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse),
        pair.Value.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))).ToArray();
    public int MaximumWriteLength => Math.Max(0, (_session?.MaxPduSize ?? 23) - 3);
    public event Action<byte[]>? NotificationReceived;
    public event Action<GattNotification>? AdditionalNotificationReceived;
    public event Action? Disconnected;
    public WindowsBluetoothConnection(BluetoothLEDevice device, IReadOnlyList<GattDeviceService> services, GattSession? session,
        BluetoothDeviceProfile profile, IReadOnlyDictionary<GattAddress, GattCharacteristic> attributes, Action<string>? diagnostic = null)
    {
        _device = device;
        _services = services;
        _session = session;
        _profile = profile;
        _attributes = attributes;
        _diagnostic = diagnostic;
        _device.ConnectionStatusChanged += OnConnectionChanged;
    }
    public async Task StartAsync(CancellationToken cancellationToken)
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
                byte[] bytes = new byte[args.CharacteristicValue.Length];
                reader.ReadBytes(bytes);
                _diagnostic?.Invoke($"GATT RX {_profile.Protocol} {endpoint.Address.Characteristic}: {Convert.ToHexString(bytes)}");
                if (endpoint.Address == _profile.Notify)
                    NotificationReceived?.Invoke(bytes);
                else
                    AdditionalNotificationReceived?.Invoke(new(endpoint.Address, bytes));
            }
            characteristic.ValueChanged += OnValue;
            _notifications.Add((characteristic, OnValue));
            bool subscribed = false;
            try
            {
                var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);
                subscribed = result == GattCommunicationStatus.Success;
                if (!subscribed && endpoint.Required)
                    throw new LocalizedOperationException("Error.BluetoothSubscribe");
            }
            catch (Exception error) when (!endpoint.Required && error is UnauthorizedAccessException or COMException)
            {
                _diagnostic?.Invoke($"GATT optional notification unavailable: {endpoint.Address.Characteristic}, HRESULT=0x{error.HResult:X8}");
            }
            if (!subscribed)
            {
                characteristic.ValueChanged -= OnValue;
                _notifications.RemoveAt(_notifications.Count - 1);
            }
        }
        if (_device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            throw new LocalizedOperationException("Error.BluetoothConnect");
        _ready = true;
    }
    public Task WriteAsync(byte[] packet, CancellationToken cancellationToken) => WriteAsync(_profile.Write, packet, cancellationToken);
    public Task WriteAsync(GattAddress address, byte[] packet, CancellationToken cancellationToken) => WriteCoreAsync(address, packet, GattWriteMode.Auto, cancellationToken);
    public Task WriteAsync(GattWrite write, CancellationToken cancellationToken) => WriteCoreAsync(write.Address, write.Data, write.Mode, cancellationToken);
    public async Task<byte[]?> ReadAsync(GattAddress address, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CheckReady();
            if (!_attributes.TryGetValue(address, out var characteristic))
                return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
            if (result.Status != GattCommunicationStatus.Success)
            {
                if (_profile.Endpoints.Any(endpoint => endpoint.Address == address && endpoint.Required))
                    throw new LocalizedIOException("Error.BluetoothState");
                return null;
            }
            using var reader = DataReader.FromBuffer(result.Value);
            byte[] bytes = new byte[result.Value.Length];
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            _gate.Release();
        }
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
    private void OnConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (_ready && !_closed && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _ready = false;
            Disconnected?.Invoke();
        }
    }
    private async Task WriteCoreAsync(GattAddress address, byte[] packet, GattWriteMode mode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var properties = GattCharacteristicProperties.None;
        ushort mtu = 23;
        try
        {
            CheckReady();
            if (!_attributes.TryGetValue(address, out var characteristic))
                throw new LocalizedOperationException("Error.BluetoothService");
            properties = characteristic.CharacteristicProperties;
            mtu = _session?.MaxPduSize ?? 23;
            var option = mode == GattWriteMode.WithResponse ? GattWriteOption.WriteWithResponse
                : mode == GattWriteMode.WithoutResponse || properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
            if (option == GattWriteOption.WriteWithResponse && !properties.HasFlag(GattCharacteristicProperties.Write) ||
                option == GattWriteOption.WriteWithoutResponse && !properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
            {
                throw new LocalizedIOException("Error.BluetoothWrite");
            }

            if (packet.Length == 0 || packet.Length > Math.Max(20, mtu - 3))
                throw new LocalizedIOException("Error.BluetoothWrite");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var writer = new DataWriter();
            writer.WriteBytes(packet);
            var buffer = writer.DetachBuffer();
            try
            {
                _diagnostic?.Invoke($"GATT TX {_profile.Protocol} {address.Characteristic}: {Convert.ToHexString(packet)}");
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
    private void CheckReady()
    {
        if (_closed || !_ready || _device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            throw new LocalizedIOException("Error.BluetoothDisconnected");
    }
}
#endif
