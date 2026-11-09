using System.Diagnostics;

namespace YokoWave.Remote;

/// <summary>Uses the server's monotonic clock; a slow response only shortens a command's validity.</summary>
internal sealed class RemoteClock(long milliseconds, long receivedAt)
{
    private Sample _sample = new(milliseconds, receivedAt);
    public long Now
    {
        get
        {
            var sample = Volatile.Read(ref _sample);
            return sample.Milliseconds + (long)Stopwatch.GetElapsedTime(sample.ReceivedAt).TotalMilliseconds;
        }
    }
    public void Update(long value) => Volatile.Write(ref _sample, new(value, Stopwatch.GetTimestamp()));
    private sealed record Sample(long Milliseconds, long ReceivedAt);
}
