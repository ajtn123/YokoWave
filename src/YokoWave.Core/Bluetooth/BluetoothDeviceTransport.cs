using System.Collections.Concurrent;
using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

/// <summary>Discovers peripherals and routes operations to independently serialized device sessions.</summary>
public sealed class BluetoothDeviceTransport : IDeviceTransport, IDeviceStatusSource,
    IElectricalDeviceFeatures, IElectricalMotionSource, IInputDeviceFeatures, IDeviceCommandTransport, IRawGattTransport
{
    private readonly IBluetoothAdapter _adapter;
    private readonly ConcurrentDictionary<string, BluetoothDeviceSession> _connections = new();
    private readonly ConcurrentDictionary<string, BluetoothPeripheral> _discovered = new();

    public bool IsSimulation => false;
    public string DisplayName => L10n.Get("Mode.Bluetooth");

    public event EventHandler<DeviceStatusUpdate>? StatusChanged;
    public event EventHandler<DeviceTransportFault>? Faulted;
    public event Action<GattDeviceNotification>? GattNotificationReceived;

    public BluetoothDeviceTransport(IBluetoothAdapter adapter) => _adapter = adapter;

    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var found = await _adapter.ScanAsync(cancellationToken).ConfigureAwait(false);
        _discovered.Clear();
        List<DeviceDescriptor> devices = [];
        foreach (var peripheral in found)
        {
            if (BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services) is not { } profile)
                continue;
            _discovered[peripheral.Id] = peripheral;
            devices.Add(Describe(peripheral, profile));
        }
        return devices;
    }

    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        if (!_discovered.TryGetValue(device.Id, out var peripheral) || BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services) is not { } profile)
            throw new LocalizedOperationException("Error.BluetoothRescan");
        if (!profile.Supported)
            throw new LocalizedOperationException("Error.PawFirmware");
        if (_connections.TryGetValue(device.Id, out var existing))
        {
            await existing.Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var link = await _adapter.ConnectAsync(peripheral, cancellationToken).ConfigureAwait(false);
        var connection = new BluetoothDeviceSession(Describe(peripheral, profile), profile, link,
            update => StatusChanged?.Invoke(this, update), fault => Faulted?.Invoke(this, fault), notification => GattNotificationReceived?.Invoke(notification));
        if (!_connections.TryAdd(device.Id, connection))
        {
            await connection.DiscardAsync().ConfigureAwait(false);
            await Get(device.Id).Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _connections.TryRemove(device.Id, out _);
            throw;
        }
    }

    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default) => Get(deviceId).SendAsync(frame, cancellationToken);
    public Task<ElectricalFeatureState> RefreshFeaturesAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Get(deviceId).WithDriverAsync(driver => driver.RefreshFeaturesAsync(cancellationToken), cancellationToken);
    public Task<bool> SetLightAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default) =>
        Get(deviceId).WithDriverAsync(driver => driver.SetLightAsync(enabled, cancellationToken), cancellationToken);
    public Task SetMotionStreamingAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default) =>
        Get(deviceId).WithDriverAsync(driver => driver.SetMotionAsync(enabled, cancellationToken), cancellationToken);
    public Task ResetInputAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Get(deviceId).WithDriverAsync(driver => driver.ResetInputAsync(cancellationToken), cancellationToken);
    public Task ExecuteCommandAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken = default) =>
        Get(deviceId).WithDriverAsync(driver => driver.ExecuteCommandAsync(command, cancellationToken), cancellationToken);
    public Task<GattDescription> OpenGattAsync(string id, Guid lease, CancellationToken cancellationToken = default) => Get(id).OpenGattAsync(lease, cancellationToken);
    public Task<byte[]?> ReadGattAsync(string id, Guid lease, GattAddress address, CancellationToken cancellationToken = default) => Get(id).ReadGattAsync(lease, address, cancellationToken);
    public Task WriteGattAsync(string id, Guid lease, IReadOnlyList<GattWrite> writes, CancellationToken cancellationToken = default) => Get(id).WriteGattAsync(lease, writes, cancellationToken);
    public Task CloseGattAsync(string id, Guid lease, CancellationToken cancellationToken = default) => Get(id).CloseGattAsync(lease, cancellationToken);

    public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (_connections.TryRemove(deviceId, out var connection))
            await connection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (string id in _connections.Keys)
        {
            try
            {
                await DisconnectAsync(id).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        if (_adapter is IAsyncDisposable disposable)
        {
            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }

    private BluetoothDeviceSession Get(string id) => _connections.TryGetValue(id, out var connection) && !connection.IsClosing
        ? connection : throw new LocalizedOperationException("Error.NotConnected");

    private static DeviceDescriptor Describe(BluetoothPeripheral peripheral, BluetoothDeviceProfile profile)
    {
        var kind = profile.Domain switch
        {
            OutputDomain.Motor => DeviceKind.Vibration,
            OutputDomain.Pump => DeviceKind.Pump,
            OutputDomain.Input => DeviceKind.Sensor,
            _ => DeviceKind.DualChannel
        };
        return new(peripheral.Id, peripheral.Name, kind, null, peripheral.Signal, profile.Protocol.ToString(), Origin: peripheral.Origin, Simulated: peripheral.Simulated);
    }
}
