using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

public partial class BluetoothDeviceTransport
{
    public event Action<GattDeviceNotification>? GattNotificationReceived;
    private void DispatchNotification(string id, Connection connection, GattNotification notification)
    {
        if (connection.Closed) return;
        if (connection.RawLease is not null && !connection.Restoring)
            GattNotificationReceived?.Invoke(new(id, notification));
        else connection.Driver.OnNotification(notification);
    }
    private static IReadOnlyList<GattAttribute> Attributes(Connection connection) => connection.Link.Attributes.Count > 0 ? connection.Link.Attributes
        : connection.Driver.Profile.Endpoints.Select(endpoint => new GattAttribute(endpoint.Address, endpoint.Read, endpoint.Write, endpoint.Write, endpoint.Notify)).ToArray();
    public async Task<GattDescription> OpenGattAsync(string id, Guid lease, CancellationToken ct = default)
    {
        if (lease == Guid.Empty) throw new LocalizedArgumentException("Error.RemoteLease");
        var connection = Get(id);
        await connection.Initialized.Task.WaitAsync(ct);
        await connection.Gate.WaitAsync(ct);
        try
        {
            if (connection.Closing || connection.RawLease is not null) throw new LocalizedOperationException("Error.DeviceInUse");
            await connection.Driver.Stop(ct);
            connection.Generation++;
            connection.RawLease = lease;
            var profile = connection.Driver.Profile;
            var peripheral = connection.Peripheral;
            var kind = profile.Domain switch { OutputDomain.Motor => DeviceKind.Vibration, OutputDomain.Pump => DeviceKind.Pump, OutputDomain.Input => DeviceKind.Sensor, _ => DeviceKind.DualChannel };
            return new(new(id, peripheral.Name, kind, null, peripheral.Signal, profile.Protocol.ToString()), Attributes(connection), connection.Link.MaximumWriteLength);
        }
        finally { connection.Gate.Release(); }
    }
    private async Task<T> WithRaw<T>(string id, Guid lease, Func<Connection, Task<T>> action, CancellationToken ct)
    {
        var connection = Get(id);
        await connection.Gate.WaitAsync(ct);
        try
        {
            if (connection.Closing || connection.Restoring || connection.RawLease != lease) throw new LocalizedOperationException("Error.RemoteLease");
            return await action(connection);
        }
        finally { connection.Gate.Release(); }
    }
    public Task<byte[]?> ReadGattAsync(string id, Guid lease, GattAddress address, CancellationToken ct = default) => WithRaw(id, lease, connection =>
    {
        if (!Attributes(connection).Any(attribute => attribute.Address == address && attribute.Read)) throw new LocalizedOperationException("Error.DeviceFeatures");
        return connection.Link.ReadAsync(address, ct);
    }, ct);
    public async Task WriteGattAsync(string id, Guid lease, IReadOnlyList<GattWrite> writes, CancellationToken ct = default)
    {
        await WithRaw(id, lease, async connection =>
        {
            if (writes is null || writes.Count is < 1 or > 16 || writes.Any(write => write is null) ||
                writes.Sum(write => (long)write.DelayAfterMilliseconds) > 100)
                throw new LocalizedArgumentException("Error.ProtocolSettings");
            foreach (var write in writes)
            {
                var attribute = Attributes(connection).FirstOrDefault(attribute => attribute.Address == write.Address);
                if (attribute is null || !attribute.Write && !attribute.WriteWithoutResponse ||
                    write.Mode == GattWriteMode.WithResponse && !attribute.Write || write.Mode == GattWriteMode.WithoutResponse && !attribute.WriteWithoutResponse ||
                    !Enum.IsDefined(write.Mode) || write.Data is null || write.Data.Length < 1 || write.Data.Length > connection.Link.MaximumWriteLength || write.DelayAfterMilliseconds is < 0 or > 100)
                    throw new LocalizedArgumentException("Error.ProtocolSettings");
            }
            await connection.Link.WriteBatchAsync(writes, ct);
            return true;
        }, ct);
    }
    public async Task CloseGattAsync(string id, Guid lease, CancellationToken ct = default)
    {
        var connection = Get(id);
        await connection.Gate.WaitAsync(ct);
        try
        {
            // An emergency zero may already have restored the host driver.
            if (connection.RawLease is null) return;
            if (connection.RawLease != lease) throw new LocalizedOperationException("Error.RemoteLease");
            await RestoreDriver(connection, id, ct);
        }
        finally { connection.Gate.Release(); }
    }
    private async Task RestoreDriver(Connection connection, string id, CancellationToken ct)
    {
        connection.Restoring = true;
        connection.Generation++;
        connection.Driver.CommandRequested -= connection.CommandRequested;
        var driver = BluetoothProtocolDriverFactory.Create(id, connection.Driver.Profile, connection.Link,
            update => StatusChanged?.Invoke(this, update), error => Faulted?.Invoke(this, new(id, false, error)));
        connection.Driver = driver;
        driver.CommandRequested += connection.CommandRequested;
        await driver.Initialize(ct);
        connection.RawLease = null;
        connection.Restoring = false;
    }
}
