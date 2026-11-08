namespace YokoWave.Core;

internal sealed record BurstPlan(double Interval, double Duration, double StrengthFactor = 1,
    WaveformPreset? WaveA = null, WaveformPreset? WaveB = null);

/// <summary>Active-clock cycle timing shared by exercise and sleep. A stalled caller starts a new wait.</summary>
internal sealed class BurstCycle(BurstPlan plan)
{
    public BurstPlan Plan { get; private set; } = plan;
    public double Started { get; private set; }
    public double BurstStarted { get; private set; }
    public double BurstEnd { get; private set; }
    public bool InBurst { get; private set; }
    private double _lastRender;
    private bool _hasRendered;

    public bool BeginRender(double elapsed)
    {
        var interrupted = _hasRendered ? elapsed - _lastRender > PlaybackInput.Timeout.TotalSeconds : elapsed > PlaybackInput.Timeout.TotalSeconds;
        _hasRendered = true; _lastRender = elapsed;
        return interrupted;
    }
    public void Restart(double elapsed, BurstPlan next) { Plan = next; Wait(elapsed); }
    public void Wait(double elapsed) { Started = elapsed; InBurst = false; BurstEnd = 0; }
    public void OnMovement(double elapsed) => Started = elapsed;
    public void Advance(double elapsed)
    {
        if (!InBurst && elapsed - Started >= Plan.Interval)
        { InBurst = true; BurstStarted = elapsed; BurstEnd = elapsed + Plan.Duration; }
    }
    public double Remaining(double elapsed) => Math.Max(0, InBurst ? BurstEnd - elapsed : Plan.Interval - (elapsed - Started));
}
