namespace YokoWave.Core.Bluetooth;

internal sealed class CoyoteV3Driver(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
    Action<DeviceStatusUpdate> publish, Action<Exception> fault) : DgOutputDriver(id, profile, link, publish, fault)
{
    private byte[]? _limits;
    private byte _stopSequence;
    public override async Task Initialize(CancellationToken ct) { await Stop(ct); await Battery(BluetoothDeviceProfiles.DgBattery, ct); }
    public override async Task Stop(CancellationToken ct)
    {
        _stopSequence = (byte)(_stopSequence % 15 + 1); var stopped = ExpectStop();
        await SetLimits(DgLabProtocol.Limits(0), ct);
        await Write(DgLabProtocol.V3(OutputFrame.Zero, _stopSequence), ct);
        await Confirm(stopped, ct, "Error.BluetoothStopState");
    }
    public override async Task Send(OutputFrame frame, CancellationToken ct)
    {
        await SetLimits(DgLabProtocol.Limits(frame.OutputLimit, frame.DgBalances), ct);
        await Write(DgLabProtocol.V3(frame), ct);
    }
    private async Task SetLimits(byte[] limits, CancellationToken ct)
    {
        if (_limits is not null && _limits.AsSpan().SequenceEqual(limits)) return;
        await Write(limits, ct); _limits = limits;
    }
    protected override (int A, int B)? ParseLevels(byte[] bytes) => DgLabProtocol.ParseV3(bytes);
    protected override bool MatchesStop(byte[] bytes) => bytes[1] == _stopSequence;
}
