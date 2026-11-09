using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Providers;
using SoundFlow.Structs;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Desktop.Audio;

internal sealed class MiniAudioInputSession : IAudioInputSession
{
    private MiniAudioEngine? _engine;
    private AudioDevice? _device;
    private SoundPlayer? _player;
    private ISoundDataProvider? _provider;
    private Stream? _stream;
    // Native lifecycle operations may wait for audio callbacks; callbacks only take the meter lock.
    private readonly Lock _controlGate = new();
    private readonly Lock _meterGate = new();
    private readonly AudioFeatureAccumulator _meter = new();
    private AudioFeatureFrame? _latest;
    private volatile bool _disposed;
    private volatile bool _paused;
    private volatile bool _ended;
    private bool _started;
    public AudioFeatureFrame? Latest => Volatile.Read(ref _latest);
    public bool Ended => _ended;
    public Exception? Error
    {
        get
        {
            lock (_controlGate)
            {
                return _started && !_paused && !_disposed && _device?.IsRunning == false
                    ? new LocalizedOperationException("Error.AudioInputLost") : null;
            }
        }
    }

    public void Start(AudioSettings settings, CancellationToken token)
    {
        lock (_controlGate)
        {
            token.ThrowIfCancellationRequested();
            if (settings.Source == AudioSourceKind.MediaFile)
                _stream = OpenMedia(settings.MediaPath);
            _engine = new MiniAudioEngine();
            token.ThrowIfCancellationRequested();
            var format = AudioFormat.DvdHq;
            if (settings.Source == AudioSourceKind.MediaFile)
            {
                StartMedia(settings, format);
            }
            else
            {
                _device = settings.Source == AudioSourceKind.SystemAudio
                    ? _engine.InitializeLoopbackDevice(format) : _engine.InitializeCaptureDevice(null, format);
                _device.OnAudioProcessed += OnCaptured;
                _device.Start();
            }
            _started = true;
            token.ThrowIfCancellationRequested();
        }
    }
    public void SetPaused(bool paused)
    {
        lock (_controlGate)
        {
            if (_disposed || _paused == paused)
                return;
            _paused = true;
            ResetMeter();
            try
            {
                if (_player is not null)
                {
                    if (paused)
                        _player.Pause();
                    else
                        _player.Play();
                }
                else if (_device is not null)
                {
                    if (paused)
                        _device.Stop();
                    else
                        _device.Start();
                }
                _paused = paused;
            }
            catch (Exception error)
            {
                throw new LocalizedOperationException("Error.AudioInputLost", error);
            }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_controlGate)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            ResetMeter();
            List<Exception> errors = [];
            void Release(Action release)
            {
                try
                {
                    release();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            if (_player is not null)
                _player.PlaybackEnded -= OnPlaybackEnded;
            if (_device is not null)
            {
                _device.OnAudioProcessed -= OnCaptured;
                Release(_device.Stop);
            }
            if (_player is not null)
                Release(_player.Dispose);
            else if (_provider is IDisposable provider)
                Release(provider.Dispose);
            if (_device is not null)
                Release(_device.Dispose);
            if (_engine is not null)
                Release(_engine.Dispose);
            if (_stream is not null)
                Release(_stream.Dispose);
            _player = null;
            _provider = null;
            _device = null;
            _engine = null;
            _stream = null;
            if (errors.Count > 0)
                throw new LocalizedAggregateException("Error.AudioCleanup", errors);
            return ValueTask.CompletedTask;
        }
    }
    private void StartMedia(AudioSettings settings, AudioFormat format)
    {
        var playback = _engine!.InitializePlaybackDevice(null, format);
        _device = playback;
        _provider = new StreamDataProvider(_engine, format, _stream!);
        _player = new SoundPlayer(_engine, format, _provider) { IsLooping = settings.LoopMedia };
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.AddAnalyzer(new FeatureAnalyzer(format, this));
        // Keep decoding and feature extraction active at 0%; mute only the final speaker buffer.
        playback.MasterMixer.AddModifier(new SpeakerGain(settings.PlaybackVolume));
        playback.MasterMixer.AddComponent(_player);
        playback.Start();
        _player.Play();
    }
    private static Stream OpenMedia(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new LocalizedOperationException("Error.AudioFileMissing");
        if (!new[] { ".wav", ".mp3", ".flac" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new LocalizedOperationException("Error.AudioFileFormat");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        try
        {
            if (stream.Length == 0)
                throw new LocalizedOperationException("Error.AudioFileEmpty");
            if (stream.Length > 100 * 1024 * 1024)
                throw new LocalizedOperationException("Error.AudioFileSize");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
    private void OnPlaybackEnded(object? sender, EventArgs args) => _ended = true;
    private void OnCaptured(Span<float> samples, Capability capability) => OnSamples(samples, 2);
    private void OnSamples(ReadOnlySpan<float> samples, int channels)
    {
        lock (_meterGate)
        {
            if (_disposed || _paused)
                return;
            _meter.Process(samples, channels);
            Volatile.Write(ref _latest, _meter.Latest);
        }
    }
    private void ResetMeter()
    {
        lock (_meterGate)
        {
            _meter.Reset();
            Volatile.Write(ref _latest, null);
        }
    }

    private sealed class FeatureAnalyzer(AudioFormat format, MiniAudioInputSession session) : AudioAnalyzer(format)
    {
        protected override void Analyze(ReadOnlySpan<float> buffer, int channels) => session.OnSamples(buffer, channels);
    }

    private sealed class SpeakerGain(double volume) : SoundModifier
    {
        private readonly AudioPlaybackGain _gain = new(volume);
        public override void Process(Span<float> buffer, int channels) => _gain.Apply(buffer);
        public override float ProcessSample(float sample, int channel) => _gain.Apply(sample);
    }
}
