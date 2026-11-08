namespace YokoWave.Core;

internal sealed class RestraintModeRuntime(ReactiveSettings settings, TimeProvider clock) : PlaybackModeRuntime, IMotionPlaybackInput
{
    private readonly Dictionary<string, MotionTracker> _targets = [];
    private readonly Dictionary<string, ReactiveStatus> _status = [];
    public override IReadOnlyDictionary<string, ReactiveStatus> Status => _status;
    public int MovementCount => 0;
    public override void Suspend() { foreach (var target in _targets.Values) target.Invalidate(); }
    public void Recalibrate(double elapsed) { foreach (var target in _targets.Values) target.Invalidate(recalibrate: true); }
    public override OutputFrame Render(PlaybackContext context, OutputFrame frame)
    {
        if (!_targets.TryGetValue(context.DeviceId, out var target)) _targets.Add(context.DeviceId, target = new());
        if (!target.Read(context.Motion, context.Elapsed.TotalSeconds, clock, out _))
        {
            _status[context.DeviceId] = new(context.DeviceId, InputPhase.Waiting);
            return frame with { ChannelA = 0, ChannelB = 0, ChannelC = 0 };
        }
        var deviation = Math.Abs(context.Motion!.Angle - target.Baseline!.Value);
        var factor = Math.Clamp((deviation - settings.SafeAngle) / settings.RampAngle, 0, 1);
        _status[context.DeviceId] = new(context.DeviceId, factor == 0 ? InputPhase.Safe : InputPhase.Tracking, context.Motion.Angle, deviation, Level: factor);
        var output = ParameterPlayback.Scale(frame, factor, factor);
        return output with { ChannelA = Math.Ceiling(output.ChannelA), ChannelB = Math.Ceiling(output.ChannelB), ChannelC = Math.Ceiling(output.ChannelC) };
    }
}
