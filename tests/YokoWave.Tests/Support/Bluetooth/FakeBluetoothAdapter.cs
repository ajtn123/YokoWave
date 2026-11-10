using YokoWave.Core.Bluetooth;

namespace YokoWave.Tests.Support.Bluetooth;

internal sealed class FakeBluetoothAdapter : IBluetoothAdapter
{
    public bool ReplyToQueries { get; init; } = true;
    public bool FailFirstWrite { get; init; }
    public Func<byte[], byte?>? Rejection { get; init; }
    public List<BluetoothPeripheral> Devices { get; } = [new("ble:000000000001", "YCY-DJ", -50)];
    public Dictionary<string, FakeBluetoothConnection> Links { get; } = [];

    public Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BluetoothPeripheral>>(Devices.ToArray());
    public Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken)
    {
        var link = new FakeBluetoothConnection
        {
            ReplyToQueries = ReplyToQueries,
            FailNextWrite = FailFirstWrite,
            Rejection = Rejection
        };
        Links[peripheral.Id] = link;
        return Task.FromResult<IBluetoothConnection>(link);
    }
}
