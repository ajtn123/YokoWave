namespace YokoWave.Core.Bluetooth;

public sealed record BluetoothPeripheral(string Id, string Name, int? Signal);

public interface IBluetoothAdapter
{
    Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct);
    Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct);
}

public interface IBluetoothConnection : IAsyncDisposable
{
    event Action<byte[]>? NotificationReceived;
    event Action? Disconnected;
    Task WriteAsync(byte[] packet, CancellationToken ct);
}

public sealed class UnsupportedBluetoothAdapter : IBluetoothAdapter
{
    public Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken ct) =>
        Task.FromException<IReadOnlyList<BluetoothPeripheral>>(new Localization.LocalizedOperationException("Error.BluetoothPlatform"));
    public Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken ct) =>
        Task.FromException<IBluetoothConnection>(new Localization.LocalizedOperationException("Error.BluetoothPlatform"));
}
