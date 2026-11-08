namespace YokoWave.Core.Bluetooth;

internal sealed class YokonexMotorDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _motors;
    public override async Task Initialize(CancellationToken ct)
    { await Stop(ct); await Write(YokonexToyProtocol.MotorInfo(), ct); await Confirm(_ready.Task, ct); }
    public override Task Stop(CancellationToken ct) => Write(YokonexToyProtocol.MotorStop(), ct);
    public override Task Send(OutputFrame frame, CancellationToken ct) => Write(YokonexToyProtocol.MotorOutput(frame, _motors), ct);
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address != Profile.Notify) return;
        var data = notification.Data;
        if (data.Length < 3 || data[0] != 0x35 || V1Protocol.WithChecksum(data.AsSpan(0, data.Length - 1))[^1] != data[^1]) return;
        if (data.Length == 10 && data[1] == 0x10 && data[2] > 0 && data[3] > 0)
        {
            var mask = (data[4] > 0 ? 1 : 0) | (data[5] > 0 ? 2 : 0) | (data[6] > 0 ? 4 : 0);
            if (mask == 0) return;
            _motors = mask; _ready.TrySetResult(); Publish(new(Id, channelMask: mask, telemetry: new(ModelId: data[2])));
        }
        else if (data.Length == 5 && data[1] == 0x13 && data[2] == 1 && data[3] <= 100) Publish(new(Id, battery: data[3]));
    }
}
