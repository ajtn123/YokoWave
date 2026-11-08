using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed record AudioFeatureFrame(long Sequence, DateTimeOffset CapturedAt, double LeftDb, double RightDb,
    double LeftOnset, double RightOnset);

public interface IAudioInputSession : IAsyncDisposable
{
    AudioFeatureFrame? Latest { get; }
    bool Ended { get; }
    Exception? Error => null;
    void SetPaused(bool paused);
}

public interface IAudioInputFactory
{
    bool SupportsSystemAudio { get; }
    Task<IAudioInputSession> StartAsync(AudioSettings settings, CancellationToken cancellationToken);
}

public sealed class UnavailableAudioInputFactory : IAudioInputFactory
{
    public bool SupportsSystemAudio => false;
    public Task<IAudioInputSession> StartAsync(AudioSettings settings, CancellationToken cancellationToken) =>
        Task.FromException<IAudioInputSession>(new LocalizedOperationException("Error.AudioUnavailable"));
}

/// <summary>Processes bounded 50 ms PCM windows; retains features, never recordings.</summary>
public sealed class AudioFeatureAccumulator(int sampleRate = 48000, TimeProvider? timeProvider = null)
{
    private readonly int _windowFrames = Math.Max(1, sampleRate / 20);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private int _frames;
    private double _leftEnergy, _rightEnergy, _previousLeftDb = -120, _previousRightDb = -120;
    private long _sequence;
    public AudioFeatureFrame? Latest { get; private set; }
    public void Reset() { _frames = 0; _leftEnergy = _rightEnergy = 0; _previousLeftDb = _previousRightDb = -120; Latest = null; }
    public void Process(ReadOnlySpan<float> samples, int channels)
    {
        if (channels < 1 || samples.Length % channels != 0) return;
        for (var offset = 0; offset < samples.Length; offset += channels)
        {
            double left = float.IsFinite(samples[offset]) ? Math.Clamp(samples[offset], -1, 1) : 0;
            double right = channels == 1 ? left : float.IsFinite(samples[offset + 1]) ? Math.Clamp(samples[offset + 1], -1, 1) : 0;
            _leftEnergy += left * left; _rightEnergy += right * right;
            if (++_frames < _windowFrames) continue;
            var leftDb = Db(_leftEnergy / _frames); var rightDb = Db(_rightEnergy / _frames);
            Latest = new(++_sequence, _clock.GetUtcNow(), leftDb, rightDb,
                Math.Clamp((leftDb - _previousLeftDb) / 24, 0, 1), Math.Clamp((rightDb - _previousRightDb) / 24, 0, 1));
            _previousLeftDb = leftDb; _previousRightDb = rightDb;
            _frames = 0; _leftEnergy = _rightEnergy = 0;
        }
    }
    private static double Db(double energy) => Math.Clamp(10 * Math.Log10(Math.Max(energy, 1e-12)), -120, 0);
}

public sealed class AudioEnvelope
{
    private bool _open;
    private double _value;
    public double Process(double db, double onset, AudioSettings settings)
    {
        if (!double.IsFinite(db) || !double.IsFinite(onset)) { Reset(); return 0; }
        var gain = .36 * (settings.Sensitivity - 50);
        var threshold = Math.Max(settings.NoiseFloorDb + (_open ? 3 : 6), -60 - gain + (_open ? 0 : 3));
        _open = db > -120 && db >= threshold;
        if (!_open) { _value = 0; return 0; }
        var target = Math.Clamp((db + gain + 60) / 48, 0, 1);
        if (settings.Follow == AudioFollowMode.Rhythm) target *= Math.Clamp(.15 + 1.5 * onset, 0, 1);
        _value += (target - _value) * (target > _value ? .5 : .3);
        return _value;
    }
    public void Reset() { _open = false; _value = 0; }
}
