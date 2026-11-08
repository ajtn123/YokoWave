namespace YokoWave.Core.Bluetooth;

public sealed record BluetoothPeripheral(string Id, string Name, int? Signal, IReadOnlyCollection<Guid>? Services = null,
    DeviceOrigin Origin = DeviceOrigin.Local, bool? Simulated = null);

public interface IBluetoothAdapter
{
    Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct);
    Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct);
}

public interface IBluetoothConnection : IAsyncDisposable
{
    int MaximumWriteLength => 20;
    IReadOnlyList<GattAttribute> Attributes => [];
    event Action<byte[]>? NotificationReceived;
    event Action? Disconnected;
    Task WriteAsync(byte[] packet, CancellationToken ct);
    event Action<GattNotification>? AdditionalNotificationReceived { add { } remove { } }
    Task WriteAsync(GattAddress address, byte[] packet, CancellationToken ct) => WriteAsync(packet, ct);
    Task<byte[]?> ReadAsync(GattAddress address, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    Task WriteAsync(GattWrite write, CancellationToken ct) => WriteAsync(write.Address, write.Data, ct);
    async Task WriteBatchAsync(IReadOnlyList<GattWrite> writes, CancellationToken ct)
    {
        foreach (var write in writes)
        {
            await WriteAsync(write, ct);
            if (write.DelayAfterMilliseconds > 0) await Task.Delay(write.DelayAfterMilliseconds, ct);
        }
    }
}

public sealed class UnsupportedBluetoothAdapter : IBluetoothAdapter
{
    public Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct) =>
        Task.FromException<IReadOnlyList<BluetoothPeripheral>>(new Localization.LocalizedOperationException("Error.BluetoothPlatform"));
    public Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct) =>
        Task.FromException<IBluetoothConnection>(new Localization.LocalizedOperationException("Error.BluetoothPlatform"));
}
