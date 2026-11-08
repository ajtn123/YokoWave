using YokoWave.Core.Localization;

namespace YokoWave.Core;

/// <summary>Serializes session lifecycle and device output. Mode instances own rendering and input state.</summary>
public sealed class SessionEngine(DeviceController controller, Random? random = null, Guid? ownerId = null)
{
    public Guid OwnerId
    { get; } = ownerId ?? Guid.NewGuid();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _random = random ?? new();
    private string[] _targets = [];
    private PlaybackModeRuntime? _mode;
    private WaveformPreset? _preset;
    private SessionProgram _program = new();
    private StrengthAutomation _strength = new(new(), random ?? new());
    private DateTimeOffset _startedAt;
    private double _peakA, _peakB, _peakC, _outputLimit = 60;
    public IReadOnlyDictionary<string, PlaybackInputStatus> InputStatus => _mode?.Status ?? EmptyStatus;
    private static readonly IReadOnlyDictionary<string, PlaybackInputStatus> EmptyStatus = new Dictionary<string, PlaybackInputStatus>();
    public int MovementCount => (_mode as IMotionPlaybackInput)?.MovementCount ?? 0;
    public const int MaxRecordingFrames = TouchModeRuntime.MaxRecordingFrames;
    public bool IsRunning
    { get; private set; }
    public bool IsPaused
    { get; private set; }
    public TimeSpan Elapsed
    { get; private set; }
    public TimeSpan? Duration
    { get; private set; }
    public TimeSpan? Remaining => Duration is { } duration ? (duration > Elapsed ? duration - Elapsed : TimeSpan.Zero) : null;
    public TimeSpan WaveformElapsed => _mode?.Waveforms?.Elapsed ?? TimeSpan.Zero;
    public WaveformPreset? CurrentWaveform => _mode?.Waveforms?.Current ?? _preset;
    public WaveformPreset? CurrentWaveformB => _mode?.Waveforms?.CurrentB ?? _preset;
    public bool UsesWaveform(Guid id) => IsRunning && _mode?.Waveforms is not null &&
        (CurrentWaveform?.Id == id || CurrentWaveformB?.Id == id || (_program.Playlist?.Any(wave => wave.Id == id) ?? false));
    public IReadOnlyList<WaveformSample> Recording => (_mode as ITouchPlaybackInput)?.Recording ?? [];
    public double ChannelA
    { get; set; }
    public double ChannelB
    { get; set; }
    public double ChannelC
    { get; set; }
    public PumpOutput Pump
    { get; set; } = new();
    public bool OffsetChannelB
    { get; set; } = true;
    public event EventHandler<SessionRecord>? Completed;

    private void ValidateWaveform(WaveformPreset preset)
    {
        preset.Validate();
        foreach (var id in _targets)
        {
            if (!controller.Connections.TryGetValue(id, out var connection))
                throw new LocalizedOperationException("Error.NotConnected");
            if (!PlaybackCompatibility.Accepts(connection.Device, preset))
                throw new LocalizedArgumentException("Error.WaveDeviceMismatch");
        }
    }

    public async Task StartAsync(IEnumerable<string> targets, WaveformPreset preset, TimeSpan? duration, SessionProgram? program = null)
    {
        preset.Validate();
        program ??= new();
        program = program with
        {
            Playlist = program.Playlist?.ToArray(),
            NativeProgram = program.NativeProgram?.Snapshot()
        };
        program.Validate();
        var mode = PlaybackModeRuntime.Create(program, preset, controller.Clock, _random);
        mode.Validate();
        if (duration is { } limit && (limit.TotalSeconds < 1 || limit.TotalSeconds > 7200))
            throw new LocalizedArgumentException("Error.Duration");
        await _gate.WaitAsync();
        try
        {
            if (IsRunning)
                throw new LocalizedOperationException("Error.AlreadyRunning");
            var ids = targets.Distinct().ToArray();
            if (ids.Length == 0 || ids.Any(id => !controller.Connections.ContainsKey(id)))
                throw new LocalizedOperationException("Error.SelectConnected");
            foreach (var id in ids)
            {
                var device = controller.Connections[id].Device;
                if (!PlaybackCompatibility.CanRun(device, program.Kind, program.Reactive))
                    throw new LocalizedOperationException("Error.ModeDeviceMismatch");
                if (program.Hardware?.Accepts(device, program.NeedsMotion) == false ||
                    program.Kind == ModeKind.NativeProgram && !(program.NativeProgram ?? new()).Accepts(device))
                    throw new LocalizedOperationException("Error.ModeDeviceMismatch");
                if (mode.Waveforms is not null)
                    foreach (var wave in new[] { preset, program.ChannelBWaveform }.OfType<WaveformPreset>().Concat(program.Playlist ?? []))
                        if (!PlaybackCompatibility.Accepts(device, wave))
                            throw new LocalizedArgumentException("Error.WaveDeviceMismatch");
            }
            if (program.Kind == ModeKind.NativeProgram)
                new NativeProgramOutput(program.NativeProgram ?? new(), 0, null).ValidateOutput(new(ChannelA, ChannelB, 20,
                    ChannelC: ChannelC, OutputLimit: program.OutputLimit, Hardware: program.Hardware));
            await controller.AcquireTargetsAsync(OwnerId, ids);
            if (program.NeedsMotion)
            {
                try
                {
                    foreach (var id in ids)
                        await controller.StartMotionOwnedAsync(OwnerId, id);
                }
                catch (Exception error)
                {
                    try
                    {
                        await controller.StopOwnedAsync(OwnerId);
                    }
                    catch (Exception stopError)
                    {
                        throw new LocalizedAggregateException("Error.StopUnconfirmed", [error, stopError]);
                    }
                    throw;
                }
            }
            _targets = ids;
            _program = program;
            _mode = mode;
            _preset = preset;
            _strength = new((program.Settings ?? new()).Strength ?? new(), _random);
            _outputLimit = program.OutputLimit;
            Pump = program.Pump ?? new();
            Duration = duration;
            Elapsed = TimeSpan.Zero;
            _startedAt = DateTimeOffset.Now;
            _peakA = _peakB = _peakC = 0;
            IsPaused = false;
            IsRunning = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> ChangeWaveformAsync(WaveformPreset preset) => ChangeWaveformAsync(preset, false);
    public Task<bool> ChangeChannelBWaveformAsync(WaveformPreset preset) => ChangeWaveformAsync(preset, true);
    private async Task<bool> ChangeWaveformAsync(WaveformPreset preset, bool channelB)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || _mode?.Waveforms is not { } waves)
                return false;
            ValidateWaveform(preset);
            waves.Change(preset, channelB);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetPausedAsync(bool paused)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || IsPaused == paused)
                return;
            IsPaused = paused;
            _strength.BoostHeld = false;
            _mode?.Suspend();
            if (paused)
                foreach (var id in _targets)
                    await controller.SendOwnedAsync(OwnerId, id, OutputFrame.Zero);
        }
        catch (Exception error)
        {
            await StopAfterErrorAsync(error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetBoostAsync(bool held)
    {
        await _gate.WaitAsync();
        try
        {
            _strength.BoostHeld = held && IsRunning && !IsPaused;
            if (IsRunning && !IsPaused)
                await SendFrameAsync();
        }
        catch (Exception error)
        {
            await StopAfterErrorAsync(error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetStrengthAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _strength.Reset(Elapsed.TotalSeconds);
            if (IsRunning && !IsPaused)
                await SendFrameAsync();
        }
        catch (Exception error)
        {
            await StopAfterErrorAsync(error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetOutputLimitAsync(double limit)
    {
        if (!double.IsFinite(limit) || limit is < 0 or > 100)
            throw new LocalizedArgumentException("Error.InvalidSettings");
        await _gate.WaitAsync();
        try
        {
            _outputLimit = limit;
            if (IsRunning && !IsPaused)
                await SendFrameAsync();
        }
        catch (Exception error)
        {
            await StopAfterErrorAsync(error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAudioFrameAsync(AudioFeatureFrame? frame)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning && !IsPaused)
                (_mode as IAudioPlaybackInput)?.SetAudio(frame);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecalibrateMotionAsync()
    {
        await _gate.WaitAsync();
        try
        {
            (_mode as IMotionPlaybackInput)?.Recalibrate(Elapsed.TotalSeconds);
            if (IsRunning && !IsPaused)
                await SendFrameAsync();
        }
        catch (Exception error)
        {
            await StopAfterErrorAsync(error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetTouchAsync(double frequency, double pulseWidth, bool held)
    {
        if (!double.IsFinite(frequency) || frequency is < 1 or > 100 || !double.IsFinite(pulseWidth) || pulseWidth is < 0 or > 100)
            throw new LocalizedArgumentException("Error.TouchParameters");
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || IsPaused || _mode is not ITouchPlaybackInput touch)
                return;
            touch.SetTouch(frequency, pulseWidth, held, Elapsed.TotalSeconds);
            await SendFrameAsync();
        }
        catch (Exception error)
        {
            await StopAfterErrorAsync(error);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AdvanceAsync(TimeSpan elapsed)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || IsPaused || _mode is null)
                return;
            if (elapsed < Elapsed)
                throw new LocalizedArgumentException("Error.Clock");
            Elapsed = elapsed;
            if (Duration is { } duration && elapsed >= duration)
            {
                Elapsed = duration;
                await StopCoreAsync("Reason.Timer");
                return;
            }
            if (_mode.Advance(elapsed) is { } completion)
            {
                await StopCoreAsync(completion);
                return;
            }
            _strength.Advance(elapsed.TotalSeconds);
            try
            {
                await SendFrameAsync();
                if (_mode.CompletionReason(_targets.Length) is { } reason)
                    await StopCoreAsync(reason);
            }
            catch (Exception error)
            {
                await StopAfterErrorAsync(error);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendFrameAsync()
    {
        if (!IsRunning || IsPaused || _mode is null)
            return;
        var settings = _program.Settings ?? new();
        var frame = new OutputFrame(_strength.Apply(ChannelA, Elapsed.TotalSeconds), _strength.Apply(ChannelB, Elapsed.TotalSeconds),
            _preset?.Frequency ?? 20, Gentle: settings.Gentle, ChannelC: _strength.Apply(ChannelC, Elapsed.TotalSeconds),
            OutputLimit: _outputLimit, DgBalances: _program.DgBalances, Hardware: _program.Hardware);
        var frames = _targets.Select(id =>
        {
            if (!controller.Connections.TryGetValue(id, out var connection))
                throw new LocalizedOperationException("Error.NotConnected");
            var output = _mode.Render(new(id, Elapsed, connection.Motion, OffsetChannelB, Remaining?.TotalSeconds, Pump), frame);
            if (Remaining is { } remaining)
                output = WaveformRendering.WithRemaining(output, remaining.TotalSeconds);
            output = output with
            {
                ChannelA = Math.Min(output.ChannelA, Math.Min(_outputLimit, _program.Hardware?.ChannelALimit ?? 100)),
                ChannelB = Math.Min(output.ChannelB, Math.Min(_outputLimit, _program.Hardware?.ChannelBLimit ?? 100)),
                ChannelC = Math.Min(output.ChannelC, _outputLimit)
            };
            output.NativeProgram?.ValidateOutput(output);
            return (Id: id, Connection: connection, Output: output);
        }).ToArray();
        var writes = frames.Select(async target =>
        {
            await controller.SendOwnedAsync(OwnerId, target.Id, target.Output);
            return target.Connection.Output;
        }).ToArray();
        try
        {
            await Task.WhenAll(writes);
        }
        finally
        {
            // Merge after all writers settle: peak updates remain serialized even without a UI synchronization context.
            foreach (var write in writes.Where(write => write.IsCompletedSuccessfully))
            {
                _peakA = Math.Max(_peakA, WaveformRendering.Strength(write.Result, 0));
                _peakB = Math.Max(_peakB, WaveformRendering.Strength(write.Result, 1));
                _peakC = Math.Max(_peakC, WaveformRendering.Strength(write.Result, 2));
            }
        }
    }

    public async Task StopAsync(string reason = "Reason.Manual")
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync(reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAfterErrorAsync(Exception error)
    {
        try
        {
            await StopCoreAsync("Reason.ConnectionError");
        }
        catch (Exception stopError)
        {
            throw new LocalizedAggregateException("Error.StopUnconfirmed", [error, stopError]);
        }
    }

    private async Task StopCoreAsync(string reason)
    {
        var wasRunning = IsRunning;
        IsRunning = IsPaused = _strength.BoostHeld = false;
        _mode?.Suspend();
        try
        {
            await controller.StopOwnedAsync(OwnerId);
        }
        finally
        {
            if (wasRunning && _mode is not null)
            {
                var waves = _mode.Waveforms;
                Completed?.Invoke(this, new(Guid.NewGuid(), _startedAt, Elapsed.TotalSeconds,
                    waves?.Current.Name ?? L10n.Get($"ModeKind.{_program.Kind}"), _targets.Length, _peakA, _peakB, reason,
                    waves?.Current.Id, waves?.SeparateB?.Id, waves?.SeparateB?.Name,
                    _program.TouchControl, (_program.Settings ?? new()).Gentle, Kind: _program.Kind, PeakC: _peakC));
            }
        }
    }
}
