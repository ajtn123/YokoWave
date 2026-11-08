using System.Collections.Concurrent;
using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

public sealed class ElectricalBluetoothTransport(IBluetoothAdapter adapter) : IDeviceTransport, IDeviceStatusSource, IElectricalDeviceFeatures, IElectricalMotionSource
{
    private sealed class Connection(IBluetoothConnection link, ElectricalDeviceProfile profile)
    {
        public IBluetoothConnection Link { get; } = link;
        public ElectricalDeviceProfile Profile { get; } = profile;
        public Action<byte[]>? NotificationHandler;
        public Action? DisconnectHandler;
        public volatile bool Closing;
        public Exception? Fault;
        public bool AOff;
        public bool BOff;
        public TaskCompletionSource ZeroConfirmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource VersionConfirmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> LightConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte HardwareVersion, SoftwareVersion;
    }

    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly Dictionary<string, BluetoothPeripheral> _discovered = [];
    public string DisplayName => L10n.Get("Mode.Bluetooth");
    public bool IsSimulation => false;
    public event EventHandler<DeviceStatusUpdate>? StatusChanged;
    public event EventHandler<DeviceTransportFault>? Faulted;

    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var found = await adapter.ScanAsync(cancellationToken);
        _discovered.Clear();
        foreach (var peripheral in found.Where(p => ElectricalDeviceProfile.FromName(p.Name) is not null)) _discovered[peripheral.Id] = peripheral;
        return _discovered.Values.Select(p => new DeviceDescriptor(p.Id, p.Name, DeviceKind.DualChannel, null, p.Signal)).ToArray();
    }

    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        if (!_discovered.TryGetValue(device.Id, out var peripheral) || ElectricalDeviceProfile.FromName(peripheral.Name) is not { } profile)
            throw new LocalizedOperationException("Error.BluetoothRescan");
        var connection = new Connection(await adapter.ConnectAsync(peripheral, cancellationToken), profile);
        connection.NotificationHandler = bytes =>
        {
            if (connection.Closing || V1Protocol.Parse(bytes) is not { } status) return;
            if (status.ErrorCode is not null)
            {
                var error = new LocalizedOperationException("Error.BluetoothRejected");
                if (Interlocked.CompareExchange(ref connection.Fault, error, null) is null)
                {
                    connection.ZeroConfirmed.TrySetException(error);
                    Faulted?.Invoke(this, new(device.Id, false, error));
                }
            }
            else
            {
                if (status.OutputAEnabled is { } a) connection.AOff = !a;
                if (status.OutputBEnabled is { } b) connection.BOff = !b;
                if (connection.AOff && connection.BOff) connection.ZeroConfirmed.TrySetResult();
                if (status.HardwareVersion is > 0 && status.SoftwareVersion is > 0)
                {
                    connection.HardwareVersion = status.HardwareVersion.Value; connection.SoftwareVersion = status.SoftwareVersion.Value;
                    connection.VersionConfirmed.TrySetResult();
                }
                if (status.LightEnabled is { } light) connection.LightConfirmed.TrySetResult(light);
                StatusChanged?.Invoke(this, new(device.Id, status.Battery, status.ContactA, status.ContactB,
                    status.OutputAEnabled, status.OutputBEnabled, status.HardwareVersion, status.SoftwareVersion, status.LightEnabled,
                    status.MotionAngle, status.Steps));
            }
        };
        connection.DisconnectHandler = () =>
        {
            if (!connection.Closing)
            {
                var error = new LocalizedIOException("Error.BluetoothDisconnected");
                connection.ZeroConfirmed.TrySetException(error);
                Faulted?.Invoke(this, new(device.Id, true, error));
            }
        };
        connection.Link.NotificationReceived += connection.NotificationHandler;
        connection.Link.Disconnected += connection.DisconnectHandler;
        if (!_connections.TryAdd(device.Id, connection)) { await CloseAsync(connection); return; }
        try
        {
            await connection.Link.WriteAsync(connection.Profile.Stop(), cancellationToken);
            foreach (var query in new byte[] { 4, 1, 2 }) await connection.Link.WriteAsync(V1Protocol.Query(query), cancellationToken);
            try { await connection.ZeroConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
            catch (TimeoutException) { throw new LocalizedIOException("Error.BluetoothState"); }
        }
        catch
        {
            _connections.TryRemove(device.Id, out _);
            try { await connection.Link.WriteAsync(connection.Profile.Stop(), CancellationToken.None); } catch { }
            await CloseAsync(connection);
            throw;
        }
    }

    public async Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(deviceId, out var connection)) throw new LocalizedOperationException("Error.NotConnected");
        var packets = connection.Profile.Encode(frame);
        for (var index = 0; index < packets.Count; index++)
        {
            if (Volatile.Read(ref connection.Fault) is { } fault && (frame.ChannelA > 0 || frame.ChannelB > 0)) throw fault;
            if (index > 0) await Task.Delay(10, cancellationToken);
            await connection.Link.WriteAsync(packets[index], cancellationToken);
        }
    }

    public async Task<ElectricalFeatureState> RefreshFeaturesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var connection = FeatureConnection(deviceId);
        if (!connection.VersionConfirmed.Task.IsCompletedSuccessfully)
        {
            await connection.Link.WriteAsync(V2Protocol.QueryVersion(), cancellationToken);
            try { await connection.VersionConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (TimeoutException) { throw new LocalizedIOException("Error.DeviceFeatures"); }
        }
        connection.LightConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await connection.Link.WriteAsync(V2Protocol.QueryLight(), cancellationToken);
        try
        {
            var light = await connection.LightConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return new(connection.HardwareVersion, connection.SoftwareVersion, light);
        }
        catch (TimeoutException) { throw new LocalizedIOException("Error.LightTimeout"); }
    }

    public async Task<bool> SetLightAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default)
    {
        var connection = FeatureConnection(deviceId);
        if (!connection.VersionConfirmed.Task.IsCompletedSuccessfully) throw new LocalizedOperationException("Error.DeviceFeatures");
        connection.LightConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await connection.Link.WriteAsync(V2Protocol.SetLight(enabled), cancellationToken);
        await connection.Link.WriteAsync(V2Protocol.QueryLight(), cancellationToken);
        bool actual;
        try { actual = await connection.LightConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
        catch (TimeoutException) { throw new LocalizedIOException("Error.LightTimeout"); }
        if (actual != enabled) throw new LocalizedIOException("Error.LightTimeout");
        return actual;
    }

    private Connection FeatureConnection(string deviceId)
    {
        if (!_connections.TryGetValue(deviceId, out var connection)) throw new LocalizedOperationException("Error.NotConnected");
        if (connection.Profile.Generation != ElectricalGeneration.CombinedV2 || Volatile.Read(ref connection.Fault) is not null)
            throw new LocalizedOperationException("Error.DeviceFeatures");
        return connection;
    }

    public async Task SetMotionStreamingAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(deviceId, out var connection)) throw new LocalizedOperationException("Error.NotConnected");
        if (enabled && Volatile.Read(ref connection.Fault) is not null) throw new LocalizedOperationException("Error.BluetoothRejected");
        await connection.Link.WriteAsync(V1Protocol.MotionStreaming(enabled), cancellationToken);
    }

    public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryRemove(deviceId, out var connection)) return;
        connection.Closing = true;
        try { await connection.Link.WriteAsync(connection.Profile.Stop(), cancellationToken); }
        finally { await CloseAsync(connection); }
    }

    private static async ValueTask CloseAsync(Connection connection)
    {
        connection.Closing = true;
        connection.Link.NotificationReceived -= connection.NotificationHandler;
        connection.Link.Disconnected -= connection.DisconnectHandler;
        await connection.Link.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (var id in _connections.Keys)
            try { await DisconnectAsync(id); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
