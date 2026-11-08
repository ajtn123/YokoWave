namespace YokoWave.Core;

/// <summary>Shared cycle and sensor plumbing. Each concrete mode supplies its own schedule and output policy.</summary>
internal abstract class ActivityModeRuntime(SessionProgram program, WaveformPreset preset, TimeProvider clock, Random random)
    : PlaybackModeRuntime, IMotionPlaybackInput
{
    public override WaveformPlayback Waveforms { get; } = new(program, preset, random);
    public override string? Advance(TimeSpan elapsed) => Waveforms.Advance(elapsed) ? "Reason.WaveComplete" : null;
    private sealed class Target(BurstPlan plan)
    {
        public MotionTracker Motion { get; } = new();
        public BurstCycle Cycle { get; } = new(plan);
    }
    private readonly Dictionary<string, Target> _targets = [];
    private readonly Dictionary<string, PlaybackInputStatus> _status = [];
    protected abstract bool NeedsMotion { get; }
    protected abstract double MovementThreshold { get; }
    protected abstract BurstPlan NextCycle();
    public override IReadOnlyDictionary<string, PlaybackInputStatus> Status => _status;
    public int MovementCount => _targets.Values.Select(target => target.Motion.Movements).DefaultIfEmpty().Min();
    protected bool AllReachedGoal(int count, int goal) => _targets.Count == count && _targets.Values.All(target => target.Motion.Movements >= goal);
    public override void Suspend()
    {
        if (NeedsMotion)
            foreach (var target in _targets.Values)
                target.Motion.Invalidate();
    }
    public void Recalibrate(double elapsed)
    {
        foreach (var target in _targets.Values)
        {
            target.Cycle.Wait(elapsed);
            target.Motion.Invalidate(recalibrate: true);
        }
    }
    public override OutputFrame Render(PlaybackContext context, OutputFrame frame)
    {
        var id = context.DeviceId;
        var elapsed = context.Elapsed.TotalSeconds;
        if (!_targets.TryGetValue(id, out var target))
            _targets.Add(id, target = new(NextCycle()));
        var cycle = target.Cycle;
        var interrupted = cycle.BeginRender(elapsed);
        if (interrupted)
        {
            cycle.Restart(elapsed, NextCycle());
            target.Motion.Invalidate();
        }
        if (NeedsMotion)
        {
            if (!target.Motion.Read(context.Motion, elapsed, clock, out var acquired))
            {
                cycle.Wait(elapsed);
                _status[id] = new(id, InputPhase.Waiting, Movements: target.Motion.Movements);
                return frame with
                {
                    ChannelA = 0,
                    ChannelB = 0,
                    ChannelC = 0
                };
            }
            if (acquired)
                cycle.OnMovement(elapsed);
            if (!cycle.InBurst && target.Motion.DetectMovement(context.Motion!, elapsed, MovementThreshold))
                cycle.OnMovement(elapsed);
        }
        if (cycle.InBurst && elapsed >= cycle.BurstEnd)
            cycle.Restart(elapsed, NextCycle());
        cycle.Advance(elapsed);
        _status[id] = new(id, cycle.InBurst ? InputPhase.Burst : InputPhase.Resting, context.Motion?.Angle,
            Movements: target.Motion.Movements, RemainingSeconds: cycle.Remaining(elapsed), Level: cycle.InBurst ? cycle.Plan.StrengthFactor : 0);
        if (!cycle.InBurst || interrupted)
            return frame with
            {
                ChannelA = 0,
                ChannelB = 0,
                ChannelC = 0
            };
        return RenderBurst(context, frame, cycle);
    }
    protected virtual OutputFrame RenderBurst(PlaybackContext context, OutputFrame frame, BurstCycle cycle) =>
        WaveformRendering.WithRemaining(Waveforms.Render(context, frame), cycle.Remaining(context.Elapsed.TotalSeconds));
}
