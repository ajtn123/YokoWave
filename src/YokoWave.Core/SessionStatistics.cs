namespace YokoWave.Core;

public sealed record SessionStatistics(long SessionCount = 0, double TotalDurationSeconds = 0)
{
    public SessionStatistics Add(SessionRecord session) => new(SessionCount + 1, TotalDurationSeconds + session.DurationSeconds);
}
