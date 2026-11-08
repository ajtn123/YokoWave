using YokoWave.Core.Localization;

namespace YokoWave.Core;

/// <summary>Caller supplies active monotonic time. Output and all lifecycle changes share one gate.</summary>
public sealed class SessionEngine(DeviceController controller, Random? random = null, Guid? ownerId = null)
{
    public Guid OwnerId { get; } = ownerId ?? Guid.NewGuid();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _random = random ?? new();
    private string[] _targets = [];
    private WaveformTrack? _a, _b;
    private SessionProgram _program = new();
    private EstimSettings _settings = new();
    private StrengthSettings _strength = new();
    private DateTimeOffset _startedAt;
    private double _peakA, _peakB, _strengthEpoch, _randomAddition;
    private long _randomBucket;
    private bool _boostHeld, _touchHeld, _hasTouch;
    private WaveformSample _touch = new(0.5, 20, 50);
    private readonly List<WaveformSample> _recording = [];
    private long _recordBucket = -1;
    private double _touchReplayEpoch;
    private double _outputLimit = 60;
    public const int MaxRecordingFrames = 256;
    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public TimeSpan? Remaining => Duration is { } duration ? (duration > Elapsed ? duration - Elapsed : TimeSpan.Zero) : null;
    public TimeSpan WaveformElapsed => _a?.Elapsed ?? TimeSpan.Zero;
    public WaveformPreset? CurrentWaveform => _a?.Current;
    public WaveformPreset? CurrentWaveformB => _b?.Current ?? _a?.Current;
    public bool UsesWaveform(Guid id) => IsRunning && !_program.TouchControl &&
        (CurrentWaveform?.Id == id || CurrentWaveformB?.Id == id || (_program.Playlist?.Any(wave => wave.Id == id) ?? false));
    public IReadOnlyList<WaveformSample> Recording => _recording.ToArray();
    public double ChannelA { get; set; }
    public double ChannelB { get; set; }
    public bool OffsetChannelB { get; set; } = true;
    public event EventHandler<SessionRecord>? Completed;

    private void ValidateWaveform(WaveformPreset preset)
    {
        preset.Validate();
        if (!controller.IsSimulation && preset.Family == WaveformFamily.Motor) throw new LocalizedArgumentException("Error.ElectricalWaveform");
    }

    public async Task StartAsync(IEnumerable<string> targets, WaveformPreset preset, TimeSpan? duration, SessionProgram? program = null)
    {
        ValidateWaveform(preset);
        program ??= new();
        var settings = program.Settings ?? new(); settings.Validate();
        if (!double.IsFinite(program.OutputLimit) || program.OutputLimit is < 0 or > 100) throw new LocalizedArgumentException("Error.InvalidSettings");
        if (program.ChannelBWaveform is { } b) ValidateWaveform(b);
        foreach (var wave in program.Playlist ?? []) ValidateWaveform(wave);
        if (!program.TouchControl && settings.Order is PlaybackOrder.Sequential or PlaybackOrder.Random)
        {
            if (program.Playlist is not { Count: > 0 }) throw new LocalizedOperationException("Error.EmptyPlaylist");
            if (program.Playlist.Any(wave => (wave.Family == WaveformFamily.Motor) != (preset.Family == WaveformFamily.Motor)) ||
                program.ChannelBWaveform is { } channelB && (channelB.Family == WaveformFamily.Motor) != (preset.Family == WaveformFamily.Motor))
                throw new LocalizedArgumentException("Error.PlaylistFamily");
        }
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) throw new LocalizedOperationException("Error.AlreadyRunning");
            var ids = targets.Distinct().ToArray();
            if (ids.Length == 0 || ids.Any(id => !controller.Connections.ContainsKey(id))) throw new LocalizedOperationException("Error.SelectConnected");
            if (duration is { } limit && (limit.TotalSeconds < 1 || limit.TotalSeconds > 7200)) throw new LocalizedArgumentException("Error.Duration");
            await controller.AcquireTargetsAsync(OwnerId, ids);
            _targets = ids; _program = program; _settings = settings; _strength = settings.Strength ?? new();
            _outputLimit = program.OutputLimit;
            var playlist = program.Playlist?.ToArray() ?? [preset];
            _a = new(preset, playlist); _b = program.ChannelBWaveform is { } separate ? new(separate, playlist) : null;
            Duration = duration; Elapsed = TimeSpan.Zero; _startedAt = DateTimeOffset.Now;
            _peakA = _peakB = _strengthEpoch = _randomAddition = 0; _randomBucket = 0;
            _boostHeld = _touchHeld = _hasTouch = IsPaused = false; _recording.Clear(); _recordBucket = -1;
            IsRunning = true;
        }
        finally { _gate.Release(); }
    }

    public Task<bool> ChangeWaveformAsync(WaveformPreset preset) => ChangeWaveformAsync(preset, false);
    public Task<bool> ChangeChannelBWaveformAsync(WaveformPreset preset) => ChangeWaveformAsync(preset, true);
    private async Task<bool> ChangeWaveformAsync(WaveformPreset preset, bool channelB)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning) return false;
            ValidateWaveform(preset);
            if (_settings.Order is PlaybackOrder.Sequential or PlaybackOrder.Random &&
                !(_program.Playlist?.Any(wave => wave.Id == preset.Id) ?? false)) throw new LocalizedOperationException("Error.PlaylistWave");
            if (channelB) { _b ??= new(preset, _program.Playlist ?? [preset]); _b.Change(preset); }
            else _a!.Change(preset);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task SetPausedAsync(bool paused)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || IsPaused == paused) return;
            IsPaused = paused; _boostHeld = _touchHeld = false;
            if (paused)
                foreach (var id in _targets) await controller.SendOwnedAsync(OwnerId, id, OutputFrame.Zero);
        }
        catch { await StopCoreAsync("Reason.ConnectionError"); throw; }
        finally { _gate.Release(); }
    }

    public async Task SetBoostAsync(bool held)
    {
        await _gate.WaitAsync();
        try { _boostHeld = held && IsRunning && !IsPaused; if (IsRunning && !IsPaused) await SendFrameAsync(); }
        catch { await StopCoreAsync("Reason.ConnectionError"); throw; }
        finally { _gate.Release(); }
    }

    public async Task ResetStrengthAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _strengthEpoch = Elapsed.TotalSeconds; _randomBucket = 0; _randomAddition = 0; _boostHeld = false;
            if (IsRunning && !IsPaused) await SendFrameAsync();
        }
        catch { await StopCoreAsync("Reason.ConnectionError"); throw; }
        finally { _gate.Release(); }
    }

    public async Task SetOutputLimitAsync(double limit)
    {
        if (!double.IsFinite(limit) || limit is < 0 or > 100) throw new LocalizedArgumentException("Error.InvalidSettings");
        await _gate.WaitAsync();
        try { _outputLimit = limit; if (IsRunning && !IsPaused) await SendFrameAsync(); }
        catch { await StopCoreAsync("Reason.ConnectionError"); throw; }
        finally { _gate.Release(); }
    }

    public async Task SetTouchAsync(double frequency, double pulseWidth, bool held)
    {
        if (!double.IsFinite(frequency) || frequency is < 1 or > 100 || !double.IsFinite(pulseWidth) || pulseWidth is < 0 or > 100)
            throw new LocalizedArgumentException("Error.TouchParameters");
        await _gate.WaitAsync();
        try
        {
            _touch = new(pulseWidth / 100, frequency, pulseWidth);
            if (!IsRunning || IsPaused || !_program.TouchControl) { _touchHeld = false; return; }
            if (held && !_touchHeld) { _recording.Clear(); _recordBucket = -1; }
            if (!held && _touchHeld) _touchReplayEpoch = Elapsed.TotalSeconds;
            _touchHeld = held; _hasTouch |= held;
            await SendFrameAsync();
        }
        catch { await StopCoreAsync("Reason.ConnectionError"); throw; }
        finally { _gate.Release(); }
    }

    public async Task AdvanceAsync(TimeSpan elapsed)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || IsPaused || _a is null) return;
            if (elapsed < Elapsed) throw new LocalizedArgumentException("Error.Clock");
            Elapsed = elapsed;
            if (Duration is { } duration && elapsed >= duration) { Elapsed = duration; await StopCoreAsync("Reason.Timer"); return; }
            _a.Advance(elapsed, _settings.Order, _random); _b?.Advance(elapsed, _settings.Order, _random);
            if (!_program.TouchControl && _a.Completed && (_b?.Completed ?? true)) { await StopCoreAsync("Reason.WaveComplete"); return; }
            var bucket = (long)Math.Floor((elapsed.TotalSeconds - _strengthEpoch) / _strength.IntervalSeconds);
            if (bucket > _randomBucket) { _randomBucket = bucket; _randomAddition = _random.NextDouble() * _strength.RandomAddition; }
            if (_program.TouchControl && _touchHeld && _recording.Count < MaxRecordingFrames)
            {
                var recordBucket = (long)Math.Floor(elapsed.TotalSeconds * 10);
                if (recordBucket != _recordBucket) { _recording.Add(_touch); _recordBucket = recordBucket; }
            }
            try { await SendFrameAsync(); }
            catch { await StopCoreAsync("Reason.ConnectionError"); throw; }
        }
        finally { _gate.Release(); }
    }

    private double Strength(double level)
    {
        if (!double.IsFinite(level) || level <= 0) return 0;
        var addition = _strength.Mode switch
        {
            StrengthMode.RandomIncrease => _randomAddition,
            StrengthMode.StepIncrease => Math.Min(Math.Max(level, _strength.Ceiling) - level,
                Math.Floor((Elapsed.TotalSeconds - _strengthEpoch) / _strength.IntervalSeconds) * _strength.Step),
            _ => 0
        };
        return Math.Clamp(level + addition + (_boostHeld ? _strength.Boost : 0), 0, 100);
    }

    private async Task SendFrameAsync()
    {
        if (!IsRunning || IsPaused || _a is null) return;
        var waveA = _a.Current; var waveB = _b?.Current ?? waveA;
        var timeA = _a.Elapsed.TotalSeconds; var timeB = _b?.Elapsed.TotalSeconds ?? timeA;
        var phaseB = _b is null && OffsetChannelB ? 0.5 : 0;
        var a = _a.Completed && !_program.TouchControl ? 0 : Strength(ChannelA);
        var b = (_b?.Completed ?? _a.Completed) && !_program.TouchControl ? 0 : Strength(ChannelB);
        WaveformSample? sampleA, sampleB;
        if (_program.TouchControl)
        {
            var touch = _touchHeld || _hasTouch && _settings.TouchPlayback == TouchPlayback.HoldLast ? _touch
                : _settings.TouchPlayback == TouchPlayback.RecordedLoop && _recording.Count > 0
                    ? _recording[(int)Math.Floor((Elapsed.TotalSeconds - _touchReplayEpoch) * 10) % _recording.Count] : null;
            sampleA = sampleB = touch;
            if (touch is null) a = b = 0;
        }
        else
        {
            sampleA = WaveformGenerator.ReferenceSample(waveA, timeA); sampleB = WaveformGenerator.ReferenceSample(waveB, timeB, phaseB);
            if (waveA.Family != WaveformFamily.Electrical) a *= WaveformGenerator.Sample(waveA, timeA);
            if (waveB.Family != WaveformFamily.Electrical) b *= WaveformGenerator.Sample(waveB, timeB, phaseB);
            // A generated B wave needs its own frequency even when A has a different preset.
            if (_b is not null && sampleB is null) sampleB = new(1, waveB.Frequency, 100);
        }
        foreach (var id in _targets)
        {
            await controller.SendOwnedAsync(OwnerId, id, new(Math.Min(a, _outputLimit), Math.Min(b, _outputLimit), waveA.Frequency, sampleA, sampleB, _settings.Gentle));
            var output = controller.Connections[id].Output;
            _peakA = Math.Max(_peakA, output.ChannelA); _peakB = Math.Max(_peakB, output.ChannelB);
        }
    }

    public async Task StopAsync(string reason = "Reason.Manual")
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(reason); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(string reason)
    {
        var wasRunning = IsRunning; IsRunning = IsPaused = _boostHeld = _touchHeld = false;
        try { await controller.StopOwnedAsync(OwnerId); }
        finally
        {
            if (wasRunning && _a is not null)
                Completed?.Invoke(this, new(Guid.NewGuid(), _startedAt, Elapsed.TotalSeconds,
                    _program.TouchControl ? L10n.Get("Touch.Title") : _a.Current.Name, _targets.Length, _peakA, _peakB, reason,
                    _program.TouchControl ? null : _a.Current.Id, _program.TouchControl ? null : _b?.Current.Id,
                    _program.TouchControl ? null : _b?.Current.Name, _program.TouchControl, _settings.Gentle));
        }
    }
}
