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
        .Add<QueryDevice>((command, cancellationToken) => command.Query is DeviceQuery.All or DeviceQuery.Battery
            ? QueryBatteryAsync(BluetoothDeviceProfiles.DgBattery, cancellationToken) : throw new Localization.LocalizedOperationException("Error.DeviceFeatures"));
    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await QueryBatteryAsync(BluetoothDeviceProfiles.DgBattery, cancellationToken).ConfigureAwait(false);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopSequence = (byte)(_stopSequence % 15 + 1);
        var stopped = ExpectStopAsync();
        await SetLimitsAsync(DgLabProtocol.Limits(0), cancellationToken).ConfigureAwait(false);
        await WriteAsync(DgLabProtocol.V3(OutputFrame.Zero, _stopSequence), cancellationToken).ConfigureAwait(false);
        await ConfirmAsync(stopped, cancellationToken, "Error.BluetoothStopState").ConfigureAwait(false);
    }
    public override async Task SendAsync(OutputFrame frame, CancellationToken cancellationToken)
    {
        await SetLimitsAsync(DgLabProtocol.Limits(Math.Min(frame.OutputLimit, frame.Hardware?.ChannelALimit ?? 100),
            Math.Min(frame.OutputLimit, frame.Hardware?.ChannelBLimit ?? 100), frame.DgBalances), cancellationToken).ConfigureAwait(false);
        if (frame.Hardware?.RelativeStrength != true)
        {
            await WriteAsync(DgLabProtocol.V3(frame), cancellationToken).ConfigureAwait(false);
            return;
        }
        int actual = Volatile.Read(ref _actual);
        var a = DgLabProtocol.StrengthUpdate.Difference(DgLabProtocol.Strength(WaveformRendering.Strength(frame, 0, true), 200, frame.Gentle), actual >> 8);
        var b = DgLabProtocol.StrengthUpdate.Difference(DgLabProtocol.Strength(WaveformRendering.Strength(frame, 1, true), 200, frame.Gentle), actual & 255);
        if (a.Value == 0 && b.Value == 0)
        {
            await WriteAsync(DgLabProtocol.V3(frame, 0, a, b), cancellationToken).ConfigureAwait(false);
            return;
        }
        _stopSequence = (byte)(_stopSequence % 15 + 1);
        _relativeSequence = _stopSequence;
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _relativeReply = reply;
        try
        {
            await WriteAsync(DgLabProtocol.V3(frame, _relativeSequence, a, b), cancellationToken).ConfigureAwait(false);
            await ConfirmAsync(reply.Task, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _relativeReply = null;
        }
    }
    protected override (int A, int B)? ParseLevels(byte[] bytes) => DgLabProtocol.ParseV3(bytes);
    protected override void AdditionalTelemetry(byte[] bytes)
    {
        if (DgLabProtocol.ParseV3(bytes) is not { } actual)
            return;
        Volatile.Write(ref _actual, actual.A << 8 | actual.B);
        if (bytes[1] == _relativeSequence)
            _relativeReply?.TrySetResult();
    }
    protected override bool MatchesStop(byte[] bytes) => bytes[1] == _stopSequence;
    private async Task SetLimitsAsync(byte[] limits, CancellationToken cancellationToken)
    {
        if (_limits is not null && _limits.AsSpan().SequenceEqual(limits))
            return;
        await WriteAsync(limits, cancellationToken).ConfigureAwait(false);
        _limits = limits;
    }
}
