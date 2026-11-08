using System.Buffers.Binary;

namespace YokoWave.Core.Bluetooth;

internal sealed class DgLabDriver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : BluetoothProtocolDriver(id, profile, link, publish, fault)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _limits;
    private TaskCompletionSource? _stopped;
    private byte _stopSequence;
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct);
        if (Profile.Protocol == BluetoothProtocol.CoyoteV2)
            await Battery(BluetoothDeviceProfiles.V2Strength, ct);
        else if (Profile.Protocol == BluetoothProtocol.Opossum)
        { await Write([0x50, 1, 1], ct); _ready.TrySetResult(); }
        else if (Profile.Domain == OutputDomain.Input)
            await Write(DgLabProtocol.SensorStream(Profile.Protocol, true), ct);
        if (Profile.Protocol != BluetoothProtocol.Paw)
            await Battery(Profile.Protocol == BluetoothProtocol.CoyoteV2 ? BluetoothDeviceProfiles.V2Battery : BluetoothDeviceProfiles.DgBattery, ct);
        await Confirm(_ready.Task, ct);
    }
    public override async Task Stop(CancellationToken ct)
    {
        switch (Profile.Protocol)
        {
            case BluetoothProtocol.CoyoteV3:
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopSequence = (byte)(_stopSequence % 15 + 1); _stopped = stopped;
                var zeroLimits = DgLabProtocol.Limits(0);
                if (_limits is null || !_limits.AsSpan().SequenceEqual(zeroLimits)) { await Write(zeroLimits, ct); _limits = zeroLimits; }
                await Write(DgLabProtocol.V3(OutputFrame.Zero, _stopSequence), ct);
                await Confirm(stopped.Task, ct, "Error.BluetoothStopState"); break;
            case BluetoothProtocol.CoyoteV2:
                _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await Link.WriteAsync(BluetoothDeviceProfiles.V2Strength, [0, 0, 0], ct);
                await Link.WriteAsync(BluetoothDeviceProfiles.V2WaveA, DgLabProtocol.V2Pulse(OutputFrame.Zero, false), ct);
                await Link.WriteAsync(BluetoothDeviceProfiles.V2WaveB, DgLabProtocol.V2Pulse(OutputFrame.Zero, true), ct);
                await Battery(BluetoothDeviceProfiles.V2Strength, ct); await Confirm(_stopped.Task, ct, "Error.BluetoothStopState"); break;
            case BluetoothProtocol.Opossum:
                await Write([0xb3, 0, 0], ct); await Write(DgLabProtocol.OpossumWave(OutputFrame.Zero), ct); break;
        }
    }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        switch (Profile.Protocol)
        {
            case BluetoothProtocol.CoyoteV3:
                var limits = DgLabProtocol.Limits(frame.OutputLimit, frame.DgBalances);
                if (_limits is null || !_limits.AsSpan().SequenceEqual(limits)) { await Write(limits, ct); _limits = limits; }
                await Write(DgLabProtocol.V3(frame), ct); break;
            case BluetoothProtocol.CoyoteV2:
                await Link.WriteAsync(BluetoothDeviceProfiles.V2Strength, DgLabProtocol.V2Strength(WaveformRendering.Strength(frame, 0), WaveformRendering.Strength(frame, 1), frame.Gentle), ct);
                await Link.WriteAsync(BluetoothDeviceProfiles.V2WaveA, DgLabProtocol.V2Pulse(frame, false), ct);
                await Link.WriteAsync(BluetoothDeviceProfiles.V2WaveB, DgLabProtocol.V2Pulse(frame, true), ct); break;
            case BluetoothProtocol.Opossum:
                await Write([0xb3, (byte)DgLabProtocol.Strength(WaveformRendering.Strength(frame, 0, true), 200, frame.Gentle), (byte)DgLabProtocol.Strength(WaveformRendering.Strength(frame, 1, true), 200, frame.Gentle)], ct);
                await Write(DgLabProtocol.OpossumWave(frame), ct); break;
        }
    }
    public override void OnNotification(GattNotification notification)
    {
        var bytes = notification.Data;
        if (notification.Address == BluetoothDeviceProfiles.DgBattery || notification.Address == BluetoothDeviceProfiles.V2Battery)
        { if (bytes.Length == 1 && bytes[0] <= 100) Publish(new(Id, battery: bytes[0])); return; }
        if (notification.Address != Profile.Notify) return;
        (int A, int B)? levels = Profile.Protocol switch
        {
            BluetoothProtocol.CoyoteV2 => DgLabProtocol.ParseV2Strength(bytes),
            BluetoothProtocol.CoyoteV3 => DgLabProtocol.ParseV3(bytes),
            BluetoothProtocol.Opossum => DgLabProtocol.ParseOpossum(bytes),
            _ => null
        };
        if (levels is { } actual)
        {
            if (actual.A == 0 && actual.B == 0)
            {
                _ready.TrySetResult();
                if (Profile.Protocol != BluetoothProtocol.CoyoteV3 || bytes[1] == _stopSequence) _stopped?.TrySetResult();
            }
            Publish(new(Id, outputAEnabled: actual.A > 0, outputBEnabled: actual.B > 0,
                telemetry: new(IntensityA: actual.A, IntensityB: actual.B)));
        }
        if (Profile.Protocol == BluetoothProtocol.Paw && bytes.Length == 4 && bytes[0] == 0x51 && bytes[2] == 3 && bytes[3] <= 100)
        { _ready.TrySetResult(); Publish(new(Id, battery: bytes[3])); }
        if (DgLabProtocol.ParseSensor(Profile.Protocol, bytes) is { } telemetry)
        { _ready.TrySetResult(); Publish(new(Id, telemetry: telemetry)); }
        if (Profile.Protocol == BluetoothProtocol.Opossum && bytes.Length == 16 && bytes[0] == 0xd0)
            Publish(new(Id, telemetry: new(Buttons: BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)))));
    }
    public override Task ResetInput(CancellationToken ct) => Profile.Protocol == BluetoothProtocol.Civet
        ? Write(DgLabProtocol.ResetPressure(), ct) : base.ResetInput(ct);
    public override Task Shutdown(CancellationToken ct) => Profile.Domain == OutputDomain.Input
        ? Write(DgLabProtocol.SensorStream(Profile.Protocol, false), ct) : Stop(ct);
}
