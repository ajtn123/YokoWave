using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

internal sealed class YokonexToyDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _motors;
    private TaskCompletionSource? _stopped;
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct);
        if (Profile.Protocol == BluetoothProtocol.YokonexMotor) await Write(YokonexToyProtocol.MotorInfo(), ct);
        else
        {
            await Write(YokonexToyProtocol.PumpCommand(4), ct);
            await Write(YokonexToyProtocol.PumpCommand(5), ct);
        }
        await Confirm(_ready.Task, ct);
    }
    public override async Task Stop(CancellationToken ct)
    {
        if (Profile.Protocol == BluetoothProtocol.YokonexMotor) { await Write(YokonexToyProtocol.MotorStop(), ct); return; }
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _stopped = stopped;
        await Write(YokonexToyProtocol.PumpCommand(3), ct);
        await Write(YokonexToyProtocol.PumpCommand(4), ct);
        await Confirm(stopped.Task, ct, "Error.BluetoothStopState");
    }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        if (Profile.Protocol == BluetoothProtocol.YokonexMotor) await Write(YokonexToyProtocol.MotorOutput(frame, _motors), ct);
        else if (frame.Pump is { } pump && (pump.Direction != PumpDirection.Off || pump.Water))
        {
            await Write(YokonexToyProtocol.PumpCommand(1, pump.Direction), ct);
            await Write(YokonexToyProtocol.PumpCommand(2, water: pump.Water), ct);
        }
        else await Stop(ct);
    }
    public override void OnNotification(GattNotification notification)
    {
        if (notification.Address != Profile.Notify) return;
        var data = notification.Data;
        if (Profile.Protocol == BluetoothProtocol.YokonexMotor)
        {
            if (data.Length < 3 || data[0] != 0x35 || V1Protocol.WithChecksum(data.AsSpan(0, data.Length - 1))[^1] != data[^1]) return;
            if (data.Length == 10 && data[1] == 0x10 && data[2] > 0 && data[3] > 0)
            {
                var mask = (data[4] > 0 ? 1 : 0) | (data[5] > 0 ? 2 : 0) | (data[6] > 0 ? 4 : 0);
                if (mask == 0) return;
                _motors = mask; _ready.TrySetResult(); Publish(new(Id, channelMask: mask, telemetry: new(ModelId: data[2])));
            }
            else if (data.Length == 5 && data[1] == 0x13 && data[2] == 1 && data[3] <= 100) Publish(new(Id, battery: data[3]));
            return;
        }
        if (YokonexToyProtocol.DecryptPump(data) is not { } plain) return;
        switch (plain[3])
        {
            case 1 when plain[4] <= 2 && plain[5] <= 1:
                if (plain[4] == 0 && plain[5] == 0) { _ready.TrySetResult(); _stopped?.TrySetResult(); }
                Publish(new(Id, outputAEnabled: plain[4] != 0, outputBEnabled: plain[5] != 0)); break;
            case 2:
                Publish(new(Id, telemetry: new(PressureA: BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(4, 2)), PressureB: BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(6, 2))))); break;
            case 3 when plain[4] <= 100: Publish(new(Id, battery: plain[4])); break;
        }
    }
}
