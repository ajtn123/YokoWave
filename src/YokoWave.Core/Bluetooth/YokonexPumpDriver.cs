using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

internal sealed class YokonexPumpDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private TaskCompletionSource? _stopped;
    public override async Task Initialize(CancellationToken ct)
    { await Stop(ct); await Write(YokonexToyProtocol.PumpCommand(4), ct); await Write(YokonexToyProtocol.PumpCommand(5), ct); }
    public override async Task Stop(CancellationToken ct)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _stopped = stopped;
        await Write(YokonexToyProtocol.PumpCommand(3), ct); await Write(YokonexToyProtocol.PumpCommand(4), ct);
        await Confirm(stopped.Task, ct, "Error.BluetoothStopState");
    }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        if (frame.Pump is { } pump && (pump.Direction != PumpDirection.Off || pump.Water))
        { await Write(YokonexToyProtocol.PumpCommand(1, pump.Direction), ct); await Write(YokonexToyProtocol.PumpCommand(2, water: pump.Water), ct); }
        else await Stop(ct);
    }
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address != Profile.Notify || YokonexToyProtocol.DecryptPump(notification.Data) is not { } plain) return;
        switch (plain[3])
        {
            case 1 when plain[4] <= 2 && plain[5] <= 1:
                if (plain[4] == 0 && plain[5] == 0) _stopped?.TrySetResult();
                Publish(new(Id, outputAEnabled: plain[4] != 0, outputBEnabled: plain[5] != 0)); break;
            case 2:
                Publish(new(Id, telemetry: new(PressureA: BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(4, 2)), PressureB: BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(6, 2))))); break;
            case 3 when plain[4] <= 100: Publish(new(Id, battery: plain[4])); break;
        }
    }
}
