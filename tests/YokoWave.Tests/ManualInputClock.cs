namespace YokoWave.Tests;

internal sealed class ManualInputClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(double seconds) => _now += TimeSpan.FromSeconds(seconds);
}
