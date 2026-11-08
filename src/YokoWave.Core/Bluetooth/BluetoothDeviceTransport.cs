using System.Collections.Concurrent;
using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

public class BluetoothDeviceTransport(IBluetoothAdapter adapter) : IDeviceTransport, IDeviceStatusSource,
    IElectricalDeviceFeatures, IElectricalMotionSource, IInputDeviceFeatures, IDeviceCommandTransport
{
    private sealed class Connection(IBluetoothConnection link, BluetoothProtocolDriver driver)
    {
        public IBluetoothConnection Link
        { get; } = link;
        public BluetoothProtocolDriver Driver
        { get; } = driver;
        public SemaphoreSlim Gate
        { get; } = new(1, 1);
        public TaskCompletionSource Initialized
        { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<byte[]>? Primary;
        public Action<GattNotification>? Additional;
        public Action? Disconnected;
        public Action<DeviceCommand>? CommandRequested;
        public volatile bool Closing, Closed;
    }
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, BluetoothPeripheral> _discovered = new();
    public bool IsSimulation => false;
    public string DisplayName => L10n.Get("Mode.Bluetooth");
    public event EventHandler<DeviceStatusUpdate>? StatusChanged;
    public event EventHandler<DeviceTransportFault>? Faulted;
    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var found = await adapter.ScanAsync(cancellationToken);
        _discovered.Clear();
        List<DeviceDescriptor> devices = [];
        foreach (var peripheral in found)
        {
            if (BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services) is not { } profile)
                continue;
            _discovered[peripheral.Id] = peripheral;
            var kind = profile.Domain switch
            {
                OutputDomain.Motor => DeviceKind.Vibration,
                OutputDomain.Pump => DeviceKind.Pump,
                OutputDomain.Input => DeviceKind.Sensor,
                _ => DeviceKind.DualChannel
            };
            devices.Add(new(peripheral.Id, peripheral.Name, kind, null, peripheral.Signal, profile.Protocol.ToString()));
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
            await existing.Initialized.Task.WaitAsync(cancellationToken);
            return;
        }
        var link = await adapter.ConnectAsync(peripheral, cancellationToken);
        void Publish(DeviceStatusUpdate update) => StatusChanged?.Invoke(this, update);
        void Fault(Exception error) => Faulted?.Invoke(this, new(device.Id, false, error));
        var driver = BluetoothProtocolDriverFactory.Create(device.Id, profile, link, Publish, Fault);
        var connection = new Connection(link, driver);
        connection.CommandRequested = command => _ = ExecuteRequestedCommandAsync(connection, command);
        driver.CommandRequested += connection.CommandRequested;
        connection.Primary = bytes =>
        {
            if (!connection.Closed) driver.OnNotification(new(profile.Notify, bytes));
        };
        connection.Additional = value =>
        {
            if (!connection.Closed) driver.OnNotification(value);
        };
        connection.Disconnected = () =>
        {
            if (!connection.Closing) Faulted?.Invoke(this, new(device.Id, true, new LocalizedIOException("Error.BluetoothDisconnected")));
        };
        link.NotificationReceived += connection.Primary;
        link.AdditionalNotificationReceived += connection.Additional;
        link.Disconnected += connection.Disconnected;
        await connection.Gate.WaitAsync();
        if (!_connections.TryAdd(device.Id, connection))
        {
            connection.Gate.Release();
            await Close(connection);
            await Get(device.Id).Initialized.Task.WaitAsync(cancellationToken);
            return;
        }
        try
        {
            await driver.Initialize(cancellationToken);
            Publish(new(device.Id, telemetry: new(MaximumWriteLength: link.MaximumWriteLength)));
            connection.Initialized.TrySetResult();
        }
        catch (Exception error)
        {
            connection.Initialized.TrySetCanceled();
            _connections.TryRemove(device.Id, out _);
            List<Exception> cleanupErrors = [];
            try
            {
                await driver.Stop(CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            try
            {
                await Close(connection);
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            if (cleanupErrors.Count > 0)
                throw new LocalizedAggregateException("Error.Cleanup", [error, .. cleanupErrors]);
            throw;
        }
        finally
        {
            connection.Gate.Release();
        }
    }
    private Connection Get(string id) => _connections.TryGetValue(id, out var connection) && !connection.Closing
        ? connection : throw new LocalizedOperationException("Error.NotConnected");
    private async Task ExecuteRequestedCommandAsync(Connection connection, DeviceCommand command)
    {
        try
        {
            await connection.Initialized.Task;
            await connection.Gate.WaitAsync();
            try
            {
                if (!connection.Closing && !connection.Closed) await connection.Driver.ExecuteCommand(command, CancellationToken.None);
            }
            finally
            {
                connection.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (!connection.Initialized.Task.IsCompletedSuccessfully) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Protocol maintenance failed: {0}", error);
        }
    }
    private async Task<T> WithConnection<T>(string id, Func<BluetoothProtocolDriver, Task<T>> action, CancellationToken ct)
    {
        var connection = Get(id);
        await connection.Initialized.Task.WaitAsync(ct);
        await connection.Gate.WaitAsync(ct);
        try
        {
            if (connection.Closing)
                throw new LocalizedIOException("Error.BluetoothDisconnected");
            return await action(connection.Driver);
        }
        finally
        {
            connection.Gate.Release();
        }
    }
    public Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default) => WithConnection(deviceId, async driver =>
    {
        if (!double.IsFinite(frame.OutputLimit) || frame.OutputLimit is < 0 or > 100)
            throw new LocalizedArgumentException("Error.InvalidSettings");
        double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, frame.OutputLimit) : 0;
        frame = frame with
        {
            ChannelA = Math.Min(Clamp(frame.ChannelA), frame.Hardware?.ChannelALimit ?? 100),
            ChannelB = Math.Min(Clamp(frame.ChannelB), frame.Hardware?.ChannelBLimit ?? 100),
            ChannelC = Clamp(frame.ChannelC)
        };
        driver.Check(frame);
        // Zero is a stop on output devices, and a no-op on sensor-only links.
        if (frame == OutputFrame.Zero)
            await driver.Stop(cancellationToken);
        else
            await driver.Send(frame, cancellationToken);
        return true;
    }, cancellationToken);
    public Task<ElectricalFeatureState> RefreshFeaturesAsync(string deviceId, CancellationToken cancellationToken = default) =>
        WithConnection(deviceId, driver => driver.RefreshFeatures(cancellationToken), cancellationToken);
    public Task<bool> SetLightAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default) =>
        WithConnection(deviceId, driver => driver.SetLight(enabled, cancellationToken), cancellationToken);
    public Task SetMotionStreamingAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default) =>
        WithConnection(deviceId, async driver =>
        {
            await driver.SetMotion(enabled, cancellationToken);
            return true;
        }, cancellationToken);
    public Task ResetInputAsync(string deviceId, CancellationToken cancellationToken = default) =>
        WithConnection(deviceId, async driver =>
        {
            await driver.ResetInput(cancellationToken);
            return true;
        }, cancellationToken);
    public Task ExecuteCommandAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken = default) =>
        WithConnection(deviceId, async driver =>
        {
            await driver.ExecuteCommand(command, cancellationToken);
            return true;
        }, cancellationToken);
    public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryRemove(deviceId, out var connection))
            return;
        await connection.Gate.WaitAsync();
        try
        {
            connection.Closing = true;
            try
            {
                await connection.Driver.Shutdown(cancellationToken);
            }
            finally
            {
                await Close(connection);
            }
        }
        finally
        {
            connection.Gate.Release();
        }
    }
    private static async ValueTask Close(Connection connection)
    {
        connection.Closing = connection.Closed = true;
        connection.Link.NotificationReceived -= connection.Primary;
        connection.Link.AdditionalNotificationReceived -= connection.Additional;
        connection.Link.Disconnected -= connection.Disconnected;
        connection.Driver.CommandRequested -= connection.CommandRequested;
        await connection.Link.DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (var id in _connections.Keys)
            try
            {
                await DisconnectAsync(id);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
