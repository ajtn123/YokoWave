namespace YokoWave.Core.Bluetooth;

internal sealed class CoyoteV3Driver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgOutputDriver(id, profile, link, publish, fault)
{
    private byte[]? _limits;
    private byte _stopSequence;
    private int _actual;
    private byte _relativeSequence;
    private TaskCompletionSource? _relativeReply;
    protected override DeviceCommandHandlers CreateCommandHandlers() => new DeviceCommandHandlers()
        .Add<QueryDevice>((command, ct) => command.Query is DeviceQuery.All or DeviceQuery.Battery
            ? Battery(BluetoothDeviceProfiles.DgBattery, ct) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures"));
    public override async Task Initialize(CancellationToken ct)
    {
        await Stop(ct);
        await Battery(BluetoothDeviceProfiles.DgBattery, ct);
    }
    public override async Task Stop(CancellationToken ct)
    {
        _stopSequence = (byte)(_stopSequence % 15 + 1);
        var stopped = ExpectStop();
        await SetLimits(DgLabProtocol.Limits(0), ct);
        await Write(DgLabProtocol.V3(OutputFrame.Zero, _stopSequence), ct);
        await Confirm(stopped, ct, "Error.BluetoothStopState");
    }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        await SetLimits(DgLabProtocol.Limits(Math.Min(frame.OutputLimit, frame.Hardware?.ChannelALimit ?? 100),
            Math.Min(frame.OutputLimit, frame.Hardware?.ChannelBLimit ?? 100), frame.DgBalances), ct);
        if (frame.Hardware?.RelativeStrength != true)
        {
            await Write(DgLabProtocol.V3(frame), ct);
            return;
        }
        var actual = Volatile.Read(ref _actual);
        var a = DgLabProtocol.StrengthUpdate.Difference(DgLabProtocol.Strength(WaveformRendering.Strength(frame, 0, true), 200, frame.Gentle), actual >> 8);
        var b = DgLabProtocol.StrengthUpdate.Difference(DgLabProtocol.Strength(WaveformRendering.Strength(frame, 1, true), 200, frame.Gentle), actual & 255);
        if (a.Value == 0 && b.Value == 0)
        {
            await Write(DgLabProtocol.V3(frame, 0, a, b), ct);
            return;
        }
        _stopSequence = (byte)(_stopSequence % 15 + 1);
        _relativeSequence = _stopSequence;
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _relativeReply = reply;
        try
        {
            await Write(DgLabProtocol.V3(frame, _relativeSequence, a, b), ct);
            await Confirm(reply.Task, ct);
        }
        finally
        {
            _relativeReply = null;
        }
    }
    private async Task SetLimits(byte[] limits, CancellationToken ct)
    {
        if (_limits is not null && _limits.AsSpan().SequenceEqual(limits)) return;
        await Write(limits, ct);
        _limits = limits;
    }
    protected override (int A, int B)? ParseLevels(byte[] bytes) => DgLabProtocol.ParseV3(bytes);
    protected override void AdditionalTelemetry(byte[] bytes)
    {
        if (DgLabProtocol.ParseV3(bytes) is not { } actual) return;
        Volatile.Write(ref _actual, actual.A << 8 | actual.B);
        if (bytes[1] == _relativeSequence) _relativeReply?.TrySetResult();
    }
    protected override bool MatchesStop(byte[] bytes) => bytes[1] == _stopSequence;
}
