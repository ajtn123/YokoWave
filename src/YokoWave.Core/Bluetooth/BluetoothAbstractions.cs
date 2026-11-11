namespace YokoWave.Core.Bluetooth;

public enum BluetoothAddressKind { Unspecified, Public, Random }

public sealed record BluetoothPeripheral(string Id, string Name, int? Signal, IReadOnlyCollection<Guid>? Services = null,
    DeviceOrigin Origin = DeviceOrigin.Local, bool? Simulated = null, BluetoothAddressKind AddressKind = BluetoothAddressKind.Unspecified, bool IsConnectable = true);

public interface IBluetoothAdapter
{
    Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken cancellationToken);
    Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken);
}

public interface IBluetoothConnection : IAsyncDisposable
{
    int MaximumWriteLength => 20;
    IReadOnlyList<GattAttribute> Attributes => [];
    event Action<byte[]>? NotificationReceived;
    event Action? Disconnected;
    Task WriteAsync(byte[] packet, CancellationToken cancellationToken);
    event Action<GattNotification>? AdditionalNotificationReceived { add { } remove { } }
    Task WriteAsync(GattAddress address, byte[] packet, CancellationToken cancellationToken) => WriteAsync(packet, cancellationToken);
    Task<byte[]?> ReadAsync(GattAddress address, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    Task WriteAsync(GattWrite write, CancellationToken cancellationToken) => WriteAsync(write.Address, write.Data, cancellationToken);
    async Task WriteBatchAsync(IReadOnlyList<GattWrite> writes, CancellationToken cancellationToken)
    {
        foreach (var write in writes)
        {
            await WriteAsync(write, cancellationToken).ConfigureAwait(false);
            if (write.DelayAfterMilliseconds > 0)
                await Task.Delay(write.DelayAfterMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class UnsupportedBluetoothAdapter : IBluetoothAdapter
{
    public Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<BluetoothPeripheral>>(new Localization.LocalizedOperationException("Error.BluetoothPlatform"));
    public Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken) =>
        Task.FromException<IBluetoothConnection>(new Localization.LocalizedOperationException("Error.BluetoothPlatform"));
}
