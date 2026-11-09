using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Tests.Support.Audio;

internal sealed class FakeAudioInputSession(TimeProvider clock) : IAudioInputSession
{
    private long _sequence;
    public bool Paused { get; private set; }
    public bool Disposed { get; private set; }
    public bool Ended { get; set; }
    public bool NoFrames { get; set; }
    public Exception? Error { get; set; }
    public double Age { get; set; }
    public double Db { get; set; } = -6;
    public bool FailPause { get; set; }
    public bool FailDispose { get; set; }
    public AudioFeatureFrame? Latest => Disposed || Paused || NoFrames ? null : new(++_sequence, clock.GetUtcNow().AddSeconds(-Age), Db, Db, 1, 1);

    public void SetPaused(bool paused)
    {
        if (FailPause)
            throw new LocalizedOperationException("Error.AudioInputLost");
        Paused = paused;
    }

    public ValueTask DisposeAsync()
    {
        if (Disposed)
            return ValueTask.CompletedTask;
        Disposed = true;
        if (FailDispose)
            throw new LocalizedIOException("Error.AudioCleanup");
        return ValueTask.CompletedTask;
    }
}
