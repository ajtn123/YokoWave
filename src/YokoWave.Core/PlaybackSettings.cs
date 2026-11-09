using YokoWave.Core.Localization;

namespace YokoWave.Core;

public enum PlaybackOrder
{
    Repeat, Once, Sequential, Random
}
public enum StrengthMode
{
    Fixed, RandomIncrease, StepIncrease
}
public enum TouchPlayback
{
    Momentary, RecordedLoop, HoldLast
}

public sealed record StrengthSettings(StrengthMode Mode = StrengthMode.Fixed, double IntervalSeconds = 10,
    double RandomAddition = 10, double Step = 1, double Ceiling = 60, double Boost = 10)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || !double.IsFinite(IntervalSeconds) || IntervalSeconds is < 1 or > 600 ||
            new[] { RandomAddition, Step, Ceiling, Boost }.Any(value => !double.IsFinite(value) || value is < 0 or > 100))
            throw new LocalizedArgumentException("Error.StrengthSettings");
    }
}
