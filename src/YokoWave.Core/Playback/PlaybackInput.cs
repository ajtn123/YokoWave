namespace YokoWave.Core;

public enum InputPhase
{
    Waiting, Safe, Tracking, Resting, Burst
}
public sealed record PlaybackInputStatus(string DeviceId, InputPhase Phase, double? Angle = null, double Deviation = 0,
    int Movements = 0, double RemainingSeconds = 0, double Level = 0);

public static class PlaybackInput
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    public static bool IsFresh(DateTimeOffset capturedAt, TimeProvider clock) =>
        clock.GetUtcNow() - capturedAt is var age && age >= TimeSpan.Zero && age <= Timeout;
}

internal static class ParameterPlayback
{
    public static WaveformSample Sample(double factor) => new(1, Math.Max(2, 100 - Math.Ceiling(factor * 100)), Math.Ceiling(factor * 100) / 100);
    public static OutputFrame Scale(OutputFrame frame, double left, double right) => frame with
    {
        ChannelA = frame.ChannelA * left,
        ChannelB = frame.ChannelB * right,
        ChannelC = frame.ChannelC * left,
        ReferenceSample = Sample(left),
        ReferenceSampleB = Sample(right),
        WindowA = null,
        WindowB = null
    };
}
