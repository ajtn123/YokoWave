using System.Diagnostics.CodeAnalysis;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class PlaybackModeViewModel
{
    private PresetItem? _selectedPresetB;
    private SessionOption? _playbackOrder, _strengthMode, _touchPlayback;
    private bool _separateWaveforms, _gentle, _showContacts = true, _boostHeld, _touchHeld;
    private double _strengthInterval = 10, _randomAddition = 10, _strengthStep = 1, _strengthCeiling = 60, _boostAmount = 10;
    private double _touchFrequency = 20, _touchPulseWidth = 50;
    private string _touchRecordingName = "";
    private bool _showStrengthOptions;
    public bool ShowStrengthOptions
    {
        get => _showStrengthOptions;
        set
        {
            if (Set(ref _showStrengthOptions, value))
                Raise(nameof(StrengthOptionsSymbol));
        }
    }
    public string StrengthOptionsSymbol => ShowStrengthOptions ? "⌃" : "⌄";
    private static readonly SessionOption[] OrderOptions = Options<PlaybackOrder>("Playback");
    private static readonly SessionOption[] StrengthOptions = Options<StrengthMode>("StrengthMode");
    private static readonly SessionOption[] TouchOptions = Options<TouchPlayback>("TouchMode");
    public IReadOnlyList<SessionOption> PlaybackOrders => OrderOptions;
    public IReadOnlyList<SessionOption> StrengthModes => StrengthOptions;
    public IReadOnlyList<SessionOption> TouchPlaybackModes => TouchOptions;
    private static SessionOption[] Options<T>(string prefix) where T : struct, Enum =>
        Enum.GetValues<T>().Select(value => new SessionOption(Convert.ToInt32(value), $"{prefix}.{value}")).ToArray();

    public SessionOption SelectedPlaybackOrder
    {
        get => _playbackOrder ?? PlaybackOrders[0];
        set
        {
            if (value is null || !CanEditRouting || !Set(ref _playbackOrder, value))
                return;
            RefreshPlaylist();
            Raise(nameof(UsesCustomPlaylist));
            Raise(nameof(ShowIndependentChannelB));
            Raise(nameof(SelectedWaveformB));
            Raise(nameof(CanOffsetChannelB));
        }
    }
    public bool UsesCustomPlaylist => IsWaveformControl && SelectedPlaybackOrder.Value is (int)PlaybackOrder.Sequential or (int)PlaybackOrder.Random;
    public bool ShowIndependentChannelB => SeparateWaveforms && !UsesCustomPlaylist;
    public SessionOption SelectedStrengthMode
    {
        get => _strengthMode ?? StrengthModes[0];
        set
        {
            if (value is not null && CanEditRouting && Set(ref _strengthMode, value))
            {
                Raise(nameof(IsRandomStrength));
                Raise(nameof(IsStepStrength));
                Raise(nameof(IsAutomatedStrength));
            }
        }
    }
    public SessionOption SelectedTouchPlayback
    {
        get => _touchPlayback ?? TouchPlaybackModes[0];
        set
        {
            if (value is not null && CanEditRouting)
                Set(ref _touchPlayback, value);
        }
    }
    public bool IsRandomStrength => SelectedStrengthMode.Value == (int)StrengthMode.RandomIncrease;
    public bool IsStepStrength => SelectedStrengthMode.Value == (int)StrengthMode.StepIncrease;
    public bool IsAutomatedStrength => IsRandomStrength || IsStepStrength;
    public bool SeparateWaveforms
    {
        get => _separateWaveforms;
        set
        {
            if (CanEditRouting && Set(ref _separateWaveforms, value))
            {
                Raise(nameof(CanOffsetChannelB));
                Raise(nameof(SelectedWaveformB));
                Raise(nameof(ShowIndependentChannelB));
                Raise(nameof(CanStart));
            }
        }
    }
    public WaveformPreset? SelectedWaveformB => ShowIndependentChannelB ? SelectedPresetB?.Preset : null;
    public bool CanOffsetChannelB => UsesWaveforms && CanEditRouting && !ShowIndependentChannelB && !(IsSleep && IsRandomSleep);
    public bool GentleMode
    {
        get => _gentle;
        set
        {
            if (CanEditRouting)
                Set(ref _gentle, value);
        }
    }
    public bool ShowContacts
    {
        get => _showContacts;
        set
        {
            Set(ref _showContacts, value);
        }
    }
    public double StrengthInterval
    {
        get => _strengthInterval;
        set => SetEstimNumber(ref _strengthInterval, value, 1, 600);
    }
    public double RandomAddition
    {
        get => _randomAddition;
        set => SetEstimNumber(ref _randomAddition, value, 0, 100);
    }
    public double StrengthStep
    {
        get => _strengthStep;
        set => SetEstimNumber(ref _strengthStep, value, 0, 100);
    }
    public double StrengthCeiling
    {
        get => _strengthCeiling;
        set => SetEstimNumber(ref _strengthCeiling, value, 0, 100);
    }
    public double BoostAmount
    {
        get => _boostAmount;
        set
        {
            SetEstimNumber(ref _boostAmount, value, 0, 100);
            Raise(nameof(BoostLabel));
        }
    }
    private void SetEstimNumber(ref double field, double value, double minimum, double maximum, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (CanEditRouting && double.IsFinite(value))
            Set(ref field, Math.Clamp(value, minimum, maximum), name);
    }
    public bool IsPaused => _engine.IsPaused;
    public bool CanInteractEstim => IsRunning && !IsPaused;
    public string PauseLabel => L10n.Get(IsPaused ? "Session.Resume" : "Session.Pause");
    public string BoostLabel => L10n.Format("Estim.BoostHold", BoostAmount);
    public bool IsBoostHeld
    {
        get => _boostHeld;
        set
        {
            if ((!value || CanInteractEstim) && Set(ref _boostHeld, value))
                _ = GuardAsync(() => _engine.SetBoostAsync(value));
        }
    }
    public bool UseTouchControl => Kind == ModeKind.Touch;
    public bool IsWaveformControl => Kind == ModeKind.Waveform;
    public bool IsTouchHeld
    {
        get => _touchHeld;
        set
        {
            if ((!value || CanInteractEstim) && Set(ref _touchHeld, value))
                _ = GuardAsync(() => _engine.SetTouchAsync(TouchFrequency, TouchPulseWidth, value));
        }
    }
    public double TouchFrequency
    {
        get => _touchFrequency;
        set
        {
            if (double.IsFinite(value))
            {
                Set(ref _touchFrequency, Math.Clamp(value, 1, 100));
                Raise(nameof(TouchCoordinates));
            }
        }
    }
    public double TouchPulseWidth
    {
        get => _touchPulseWidth;
        set
        {
            if (double.IsFinite(value))
            {
                Set(ref _touchPulseWidth, Math.Clamp(value, 0, 100));
                Raise(nameof(TouchCoordinates));
            }
        }
    }
    public string TouchCoordinates => L10n.Format("Touch.Coordinates", TouchFrequency, TouchPulseWidth);
    public string RecordingSummary => L10n.Format("Touch.RecordedFrames", _engine.Recording.Count, SessionEngine.MaxRecordingFrames);
    public bool HasRecording => _engine.Recording.Count >= 2;
    public string TouchRecordingName
    {
        get => _touchRecordingName;
        set
        {
            Set(ref _touchRecordingName, value);
            SaveRecordingCommand.NotifyCanExecuteChanged();
        }
    }
    public bool CanSaveRecording => !IsRunning && HasRecording && !string.IsNullOrWhiteSpace(TouchRecordingName);
    public AsyncCommand PauseCommand
    { get; private set; }
    public AsyncCommand ResetStrengthCommand
    { get; private set; }
    public AsyncCommand SaveRecordingCommand
    { get; private set; }

    public PresetItem? SelectedPresetB
    {
        get => _selectedPresetB;
        set
        {
            if (value is not null && UsesWaveforms && !AcceptsWaveform(value.Preset))
            {
                _host.ShowValidationError("Error.WaveDeviceMismatch");
                Raise(nameof(SelectedPresetB));
                return;
            }
            if (value is null || !Set(ref _selectedPresetB, value))
                return;
            Raise(nameof(SelectedWaveformB));
            Raise(nameof(CanStart));
            if (IsRunning && SeparateWaveforms)
                _ = GuardAsync(() => ApplyChannelBAsync(value));
        }
    }
    private async Task ApplyChannelBAsync(PresetItem item)
    {
        try
        {
            await _engine.ChangeChannelBWaveformAsync(item.Preset);
        }
        catch
        {
            _selectedPresetB = Presets.FirstOrDefault(preset => preset.Preset.Id == _engine.CurrentWaveformB?.Id);
            Raise(nameof(SelectedPresetB));
            Raise(nameof(SelectedWaveformB));
            throw;
        }
    }
    [MemberNotNull(nameof(PauseCommand), nameof(ResetStrengthCommand), nameof(SaveRecordingCommand))]
    private void InitializeEstimCommands()
    {
        PauseCommand = new(TogglePauseAsync, ShowError, () => IsRunning);
        ResetStrengthCommand = new(async () =>
        {
            await _engine.ResetStrengthAsync();
            RefreshPlayback();
        }, ShowError, () => CanInteractEstim);
        SaveRecordingCommand = new(SaveRecordingAsync, ShowError, () => CanSaveRecording);
    }
    private void InitializeEstim(EstimSettings? settings)
    {
        settings ??= new();
        var strength = settings.Strength ?? new();
        SelectedPlaybackOrder = PlaybackOrders.First(option => option.Value == (int)settings.Order);
        SelectedStrengthMode = StrengthModes.First(option => option.Value == (int)strength.Mode);
        SelectedTouchPlayback = TouchPlaybackModes.First(option => option.Value == (int)settings.TouchPlayback);
        SeparateWaveforms = settings.SeparateWaveforms;
        SelectedPresetB = Presets.FirstOrDefault();
        GentleMode = settings.Gentle;
        ShowContacts = settings.ShowContacts;
        StrengthInterval = strength.IntervalSeconds;
        RandomAddition = strength.RandomAddition;
        StrengthStep = strength.Step;
        StrengthCeiling = strength.Ceiling;
        BoostAmount = strength.Boost;
    }
    private EstimSettings CreateEstimSettings() => new((PlaybackOrder)SelectedPlaybackOrder.Value,
        new((StrengthMode)SelectedStrengthMode.Value, StrengthInterval, RandomAddition, StrengthStep, StrengthCeiling, BoostAmount),
        SeparateWaveforms, GentleMode, ShowContacts, (TouchPlayback)SelectedTouchPlayback.Value);
    private SessionProgram CreateSessionProgram()
    {
        var settings = CreateEstimSettings();
        var b = UsesWaveforms && ShowIndependentChannelB && !(IsSleep && IsRandomSleep) && HasChannelB ? SelectedPresetB?.Preset : null;
        var usesPlaylist = UsesCustomPlaylist || IsSleep && IsRandomSleep;
        var playlist = usesPlaylist
            ? Playlist.Select(item => item.Preset.Preset).ToArray()
            : new[] { SelectedPreset?.Preset, b }.OfType<WaveformPreset>().DistinctBy(preset => preset.Id).ToArray();
        if (usesPlaylist && playlist.Length == 0)
            throw new LocalizedOperationException("Error.EmptyPlaylist");
        return new(settings, playlist, b, Kind, OutputLimit, _audioSettings, _reactiveSettings, IsPump ? _pump : null, _balances, IsNativeProgram ? NativeEditor.Settings : null, HardwareEditor.Settings);
    }
    public async Task TogglePauseAsync()
    {
        try
        {
            await _engine.SetPausedAsync(!IsPaused);
            _input.SetPaused(IsPaused);
            if (IsPaused || !IsRunning)
                _clock.Stop();
            else
                _clock.Start();
        }
        catch (Exception error)
        {
            try
            {
                await StopAsync("Reason.InputLost", false);
            }
            catch (Exception stopError)
            {
                throw new LocalizedAggregateException("Error.StopUnconfirmed", [error, stopError]);
            }
            throw;
        }
        finally
        {
            RefreshPlayback();
        }
    }
    public async Task SaveRecordingAsync()
    {
        if (!CanSaveRecording)
            throw new LocalizedOperationException("Error.TouchRecording");
        var samples = _engine.Recording.ToArray();
        var preset = new WaveformPreset(Guid.NewGuid(), TouchRecordingName.Trim(), WaveShape.Sampled, samples.Length / 10.0,
            TouchFrequency, Samples: samples);
        preset.Validate();
        await _host.SaveAsync(Presets.Select(item => item.Preset).Append(preset));
        var item = new PresetItem(preset);
        Presets.Add(item);
        SelectedPreset = item;
        Notify("Status.WaveSaved", preset.Name);
    }
    private void RefreshEstimPlayback()
    {
        if (IsRunning && UsesWaveforms)
        {
            var a = Presets.FirstOrDefault(item => item.Preset.Id == _engine.CurrentWaveform?.Id);
            if (a is not null && a != _selectedPreset)
            {
                _selectedPreset = a;
                foreach (var name in new[] { nameof(SelectedPreset), nameof(SelectedPresetName), nameof(SelectedWaveform), nameof(PresetSummary) })
                    Raise(name);
                DeleteWaveCommand.NotifyCanExecuteChanged();
            }
            var b = Presets.FirstOrDefault(item => item.Preset.Id == _engine.CurrentWaveformB?.Id);
            if (b is not null && b != _selectedPresetB)
            {
                _selectedPresetB = b;
                Raise(nameof(SelectedPresetB));
                Raise(nameof(SelectedWaveformB));
            }
        }
        if (!CanInteractEstim)
        {
            if (_boostHeld)
            {
                _boostHeld = false;
                Raise(nameof(IsBoostHeld));
            }
            if (_touchHeld)
            {
                _touchHeld = false;
                Raise(nameof(IsTouchHeld));
            }
        }
        foreach (var name in new[] { nameof(IsPaused), nameof(CanInteractEstim), nameof(PauseLabel), nameof(CanOffsetChannelB), nameof(RecordingSummary), nameof(HasRecording), nameof(CanSaveRecording) })
            Raise(name);
        PauseCommand.NotifyCanExecuteChanged();
        ResetStrengthCommand.NotifyCanExecuteChanged();
        SaveRecordingCommand.NotifyCanExecuteChanged();
    }
    private void RefreshEstimLocalization()
    {
        foreach (var option in PlaybackOrders.Concat(StrengthModes).Concat(TouchPlaybackModes))
            option.RefreshLocalization();
        foreach (var name in new[] { nameof(PauseLabel), nameof(BoostLabel), nameof(TouchCoordinates), nameof(RecordingSummary), nameof(SelectedWaveformB) })
            Raise(name);
    }
}

public sealed class SessionOption(int value, string key, params object?[] arguments) : ObservableObject
{
    public int Value
    { get; } = value;
    public string Label => arguments.Length == 0 ? L10n.Get(key) : L10n.Format(key, arguments);
    public override string ToString() => Label;
    public void RefreshLocalization() => Raise(nameof(Label));
}
