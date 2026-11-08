using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.Playback;

internal sealed class AudioModeInputController(IAudioInputFactory factory, Func<AudioSettings> settings, TimeProvider clock) : ModeInputController
{
    private IAudioInputSession? _session;
    private CancellationTokenSource? _cancellation;
    public bool Ended => _session?.Ended == true;
    public AudioFeatureFrame? CurrentFrame => _session is { Ended: false, Latest: { } frame } && PlaybackInput.IsFresh(frame.CapturedAt, clock) ? frame : null;

    public override async Task StartAsync()
    {
        var cancellation = new CancellationTokenSource(); var token = cancellation.Token; _cancellation = cancellation;
        var session = await factory.StartAsync(settings(), token);
        if (token.IsCancellationRequested || _cancellation != cancellation)
        { await session.DisposeAsync(); throw new OperationCanceledException(token); }
        _session = session;
    }
    public override Task UpdateAsync(SessionEngine engine)
    {
        if (_session is null) throw new LocalizedOperationException("Error.AudioInputLost");
        if (!_session.Ended && _session.Error is { } error) throw error;
        // Silence, callback gaps and EOF remain waiting states until the user stops or a real fault occurs.
        return engine.SetAudioFrameAsync(CurrentFrame);
    }
    public override void SetPaused(bool paused) => _session?.SetPaused(paused);
    public override void Cancel() => _cancellation?.Cancel();
    public override async ValueTask DisposeAsync()
    {
        var cancellation = _cancellation; _cancellation = null;
        cancellation?.Cancel(); cancellation?.Dispose();
        var session = _session; _session = null;
        if (session is not null) await session.DisposeAsync();
    }
    public async Task<double> CalibrateAsync()
    {
        await StartAsync(); var token = _cancellation!.Token; long sequence = -1;
        var levels = new List<double>();
        for (var step = 0; step < 20; step++)
        {
            await Task.Delay(100, token);
            if (CurrentFrame is { } frame && frame.Sequence != sequence &&
                double.IsFinite(frame.LeftDb) && double.IsFinite(frame.RightDb) && frame.LeftDb is >= -120 and <= 0 && frame.RightDb is >= -120 and <= 0)
            { sequence = frame.Sequence; levels.Add(Math.Max(frame.LeftDb, frame.RightDb)); }
        }
        if (levels.Count == 0) throw new LocalizedOperationException("Error.AudioCalibrationNoData");
        levels.Sort(); return Math.Clamp(levels[(int)((levels.Count - 1) * .75)], -90, -30);
    }
}
