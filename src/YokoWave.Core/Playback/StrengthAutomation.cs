namespace YokoWave.Core;

internal sealed class StrengthAutomation(StrengthSettings settings, Random random)
{
    private double _epoch, _randomAddition;
    private long _randomBucket;
    public bool BoostHeld { get; set; }
    public void Reset(double elapsed)
    {
        _epoch = elapsed; _randomBucket = 0; _randomAddition = 0; BoostHeld = false;
    }
    public void Advance(double elapsed)
    {
        var bucket = (long)Math.Floor((elapsed - _epoch) / settings.IntervalSeconds);
        if (bucket <= _randomBucket) return;
        _randomBucket = bucket; _randomAddition = random.NextDouble() * settings.RandomAddition;
    }
    public double Apply(double level, double elapsed)
    {
        if (!double.IsFinite(level) || level <= 0) return 0;
        var addition = settings.Mode switch
        {
            StrengthMode.RandomIncrease => _randomAddition,
            StrengthMode.StepIncrease => Math.Min(Math.Max(level, settings.Ceiling) - level,
                Math.Floor((elapsed - _epoch) / settings.IntervalSeconds) * settings.Step),
            _ => 0
        };
        return Math.Clamp(level + addition + (BoostHeld ? settings.Boost : 0), 0, 100);
    }
}
