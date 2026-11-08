namespace YokoWave.Core;

internal sealed class SleepModeRuntime(SessionProgram program, WaveformPreset preset, TimeProvider clock, Random random)
    : ActivityModeRuntime(program, preset, clock, random)
{
    private readonly ReactiveSettings _settings = program.Reactive ?? new();
    private readonly IReadOnlyList<WaveformPreset> _pool = program.Playlist ?? [];
    private readonly Random _random = random;
    protected override bool NeedsMotion => _settings.SleepCycle == SleepCycleKind.ActivityReset;
    protected override double MovementThreshold => 1;
    public override void Validate()
    {
        if (_settings.SleepCycle == SleepCycleKind.Random && _pool.Count == 0)
            throw new Localization.LocalizedOperationException("Error.EmptyPlaylist");
    }
    protected override BurstPlan NextCycle()
    {
        if (_settings.SleepCycle != SleepCycleKind.Random) return new(_settings.IntervalSeconds, _settings.BurstSeconds);
        return new(Between(_settings.MinimumIntervalSeconds, _settings.MaximumIntervalSeconds),
            Between(_settings.MinimumBurstSeconds, _settings.MaximumBurstSeconds),
            Between(_settings.MinimumStrengthPercent, _settings.MaximumStrengthPercent) / 100,
            _pool[_random.Next(_pool.Count)], _pool[_random.Next(_pool.Count)]);
    }
    private double Between(double min, double max) => min + _random.NextDouble() * (max - min);
    protected override OutputFrame RenderBurst(PlaybackContext context, OutputFrame frame, BurstCycle cycle)
    {
        if (cycle.Plan is not { WaveA: { } a, WaveB: { } b } plan) return base.RenderBurst(context, frame, cycle);
        var time = context.Elapsed.TotalSeconds - cycle.BurstStarted;
        var remaining = cycle.Remaining(context.Elapsed.TotalSeconds);
        return frame with
        {
            ChannelA = frame.ChannelA * plan.StrengthFactor,
            ChannelB = frame.ChannelB * plan.StrengthFactor,
            ChannelC = frame.ChannelC * plan.StrengthFactor,
            Frequency = a.Frequency,
            ReferenceSample = WaveformGenerator.At(a, time),
            ReferenceSampleB = WaveformGenerator.At(b, time),
            WindowA = WaveformGenerator.Window(a, time, remainingSeconds: remaining),
            WindowB = WaveformGenerator.Window(b, time, remainingSeconds: remaining)
        };
    }
}
