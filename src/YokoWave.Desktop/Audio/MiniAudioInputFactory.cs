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

public sealed class MiniAudioInputFactory : IAudioInputFactory
{
    public bool SupportsSystemAudio => OperatingSystem.IsWindows();
    public Task<IAudioInputSession> StartAsync(AudioSettings settings, CancellationToken cancellationToken) =>
        Task.Run<IAudioInputSession>(() =>
        {
            settings.Validate(); cancellationToken.ThrowIfCancellationRequested();
            if (settings.Source == AudioSourceKind.SystemAudio && !SupportsSystemAudio)
                throw new LocalizedOperationException("Error.SystemAudioPlatform");
            var session = new Session();
            try { session.Start(settings, cancellationToken); return session; }
            catch (Exception error)
            {
                session.DisposeAsync().GetAwaiter().GetResult();
                if (error is ILocalizedError or OperationCanceledException) throw;
                throw new LocalizedOperationException("Error.AudioInputFailed");
            }
        }, cancellationToken);

    private sealed class FeatureAnalyzer(AudioFormat format, Session session) : AudioAnalyzer(format)
    {
        protected override void Analyze(ReadOnlySpan<float> buffer, int channels) => session.OnSamples(buffer, channels);
    }

    private sealed class Session : IAudioInputSession
    {
        private MiniAudioEngine? _engine;
        private AudioDevice? _device;
        private SoundPlayer? _player;
        private ISoundDataProvider? _provider;
        private Stream? _stream;
        private readonly AudioFeatureAccumulator _meter = new();
        private readonly object _meterGate = new();
        private AudioFeatureFrame? _latest;
        private bool _disposed, _paused, _started;
        private volatile bool _ended;
        public AudioFeatureFrame? Latest => Volatile.Read(ref _latest);
        public bool Ended => _ended;
        public Exception? Error => _started && !_paused && !_disposed && _device?.IsRunning == false
            ? new LocalizedOperationException("Error.AudioInputLost") : null;
        public void Start(AudioSettings settings, CancellationToken cancellationToken)
        {
            var format = AudioFormat.DvdHq;
            if (settings.Source == AudioSourceKind.MediaFile)
            {
                var path = settings.MediaPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new LocalizedOperationException("Error.AudioFileMissing");
                if (!new[] { ".wav", ".mp3", ".flac" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    throw new LocalizedOperationException("Error.AudioFileFormat");
                if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new LocalizedOperationException("Error.AudioFileSize");
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            _engine = new MiniAudioEngine();
            cancellationToken.ThrowIfCancellationRequested();
            if (settings.Source == AudioSourceKind.MediaFile)
            {
                var playback = _engine.InitializePlaybackDevice(null, format);
                _device = playback;
                _provider = new StreamDataProvider(_engine, format, _stream!);
                _player = new SoundPlayer(_engine, format, _provider) { IsLooping = settings.LoopMedia };
                _player.PlaybackEnded += (_, _) => _ended = true;
                _player.AddAnalyzer(new FeatureAnalyzer(format, this));
                playback.MasterMixer.Volume = (float)(settings.PlaybackVolume / 100);
                playback.MasterMixer.AddComponent(_player);
                playback.Start(); _player.Play();
            }
            else
            {
                _device = settings.Source == AudioSourceKind.SystemAudio
                    ? _engine.InitializeLoopbackDevice(format) : _engine.InitializeCaptureDevice(null, format);
                _device.OnAudioProcessed += OnCaptured;
                _device.Start();
            }
            _started = true; cancellationToken.ThrowIfCancellationRequested();
        }
        private void OnCaptured(Span<float> samples, Capability capability) => OnSamples(samples, 2);
        internal void OnSamples(ReadOnlySpan<float> samples, int channels)
        {
            lock (_meterGate)
            {
                if (_disposed || _paused) return;
                _meter.Process(samples, channels); Volatile.Write(ref _latest, _meter.Latest);
            }
        }
        public void SetPaused(bool paused)
        {
            if (_disposed || _paused == paused) return;
            _paused = paused;
            if (_player is not null) { if (paused) _player.Pause(); else _player.Play(); }
            else if (_device is not null) { if (paused) _device.Stop(); else _device.Start(); }
            lock (_meterGate) { _meter.Reset(); Volatile.Write(ref _latest, null); }
        }
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            try { _device?.Stop(); }
            finally
            {
                try { if (_player is not null) _player.Dispose(); else (_provider as IDisposable)?.Dispose(); }
                finally
                {
                    try { _device?.Dispose(); }
                    finally { try { _engine?.Dispose(); } finally { _stream?.Dispose(); } }
                }
                Volatile.Write(ref _latest, null);
            }
            return ValueTask.CompletedTask;
        }
    }
}
