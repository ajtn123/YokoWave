using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class MainViewModel
{
    private PresetItem? _selectedPresetB;
    private SessionOption? _playbackOrder, _strengthMode, _touchPlayback;
    private bool _separateWaveforms, _gentle, _showContacts = true, _useTouchControl, _boostHeld, _touchHeld;
    private double _strengthInterval = 10, _randomAddition = 10, _strengthStep = 1, _strengthCeiling = 60, _boostAmount = 10;
    private double _touchFrequency = 20, _touchPulseWidth = 50;
    private string _touchRecordingName = "";
    private bool _showStrengthOptions;
    public bool ShowStrengthOptions { get => _showStrengthOptions; set { if (Set(ref _showStrengthOptions, value)) Raise(nameof(StrengthOptionsSymbol)); } }
    public string StrengthOptionsSymbol => ShowStrengthOptions ? "⌃" : "⌄";
    public IReadOnlyList<SessionOption> PlaybackOrders { get; } = Options<PlaybackOrder>("Playback");
    public IReadOnlyList<SessionOption> StrengthModes { get; } = Options<StrengthMode>("StrengthMode");
    public IReadOnlyList<SessionOption> TouchPlaybackModes { get; } = Options<TouchPlayback>("TouchMode");
    private static SessionOption[] Options<T>(string prefix) where T : struct, Enum =>
        Enum.GetValues<T>().Select(value => new SessionOption(Convert.ToInt32(value), $"{prefix}.{value}")).ToArray();

    public SessionOption SelectedPlaybackOrder { get => _playbackOrder ?? PlaybackOrders[0]; set { if (!IsRunning) Set(ref _playbackOrder, value); } }
    public SessionOption SelectedStrengthMode
    {
        get => _strengthMode ?? StrengthModes[0];
        set { if (!IsRunning && Set(ref _strengthMode, value)) { Raise(nameof(IsRandomStrength)); Raise(nameof(IsStepStrength)); Raise(nameof(IsAutomatedStrength)); } }
    }
    public SessionOption SelectedTouchPlayback { get => _touchPlayback ?? TouchPlaybackModes[0]; set { if (!IsRunning) Set(ref _touchPlayback, value); } }
    public bool IsRandomStrength => SelectedStrengthMode.Value == (int)StrengthMode.RandomIncrease;
    public bool IsStepStrength => SelectedStrengthMode.Value == (int)StrengthMode.StepIncrease;
    public bool IsAutomatedStrength => IsRandomStrength || IsStepStrength;
    public bool SeparateWaveforms { get => _separateWaveforms; set { if (!IsRunning && Set(ref _separateWaveforms, value)) { Raise(nameof(CanOffsetChannelB)); Raise(nameof(SelectedWaveformB)); } } }
    public WaveformPreset? SelectedWaveformB => SeparateWaveforms ? SelectedPresetB?.Preset : null;
    public bool CanOffsetChannelB => CanEditRouting && !SeparateWaveforms && !UseTouchControl;
    public bool GentleMode { get => _gentle; set { if (!IsRunning) Set(ref _gentle, value); } }
    public bool ShowContacts { get => _showContacts; set { if (Set(ref _showContacts, value)) Raise(nameof(HasElectrodeReadings)); } }
    public double StrengthInterval { get => _strengthInterval; set => SetEstimNumber(ref _strengthInterval, value, 1, 600); }
    public double RandomAddition { get => _randomAddition; set => SetEstimNumber(ref _randomAddition, value, 0, 100); }
    public double StrengthStep { get => _strengthStep; set => SetEstimNumber(ref _strengthStep, value, 0, 100); }
    public double StrengthCeiling { get => _strengthCeiling; set => SetEstimNumber(ref _strengthCeiling, value, 0, 100); }
    public double BoostAmount { get => _boostAmount; set { SetEstimNumber(ref _boostAmount, value, 0, 100); Raise(nameof(BoostLabel)); } }
    private void SetEstimNumber(ref double field, double value, double minimum, double maximum, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    { if (!IsRunning && double.IsFinite(value)) Set(ref field, Math.Clamp(value, minimum, maximum), name); }
    public bool IsPaused => _engine.IsPaused;
    public bool CanInteractEstim => IsRunning && !IsPaused;
    public string PauseLabel => L10n.Get(IsPaused ? "Session.Resume" : "Session.Pause");
    public string BoostLabel => L10n.Format("Estim.BoostHold", BoostAmount);
    public bool IsBoostHeld
    {
        get => _boostHeld;
        set { if ((!value || CanInteractEstim) && Set(ref _boostHeld, value)) _ = GuardAsync(() => _engine.SetBoostAsync(value)); }
    }
    public bool UseTouchControl
    {
        get => _useTouchControl;
        set { if (!IsRunning && Set(ref _useTouchControl, value)) { Raise(nameof(IsWaveformControl)); Raise(nameof(CanOffsetChannelB)); Raise(nameof(SelectedPresetName)); } }
    }
    public bool IsWaveformControl => !UseTouchControl;
    public bool IsTouchHeld
    {
        get => _touchHeld;
        set { if ((!value || CanInteractEstim) && Set(ref _touchHeld, value)) _ = GuardAsync(() => _engine.SetTouchAsync(TouchFrequency, TouchPulseWidth, value)); }
    }
    public double TouchFrequency { get => _touchFrequency; set { if (double.IsFinite(value)) { Set(ref _touchFrequency, Math.Clamp(value, 1, 100)); Raise(nameof(TouchCoordinates)); } } }
    public double TouchPulseWidth { get => _touchPulseWidth; set { if (double.IsFinite(value)) { Set(ref _touchPulseWidth, Math.Clamp(value, 0, 100)); Raise(nameof(TouchCoordinates)); } } }
    public string TouchCoordinates => L10n.Format("Touch.Coordinates", TouchFrequency, TouchPulseWidth);
    public string RecordingSummary => L10n.Format("Touch.RecordedFrames", _engine.Recording.Count, SessionEngine.MaxRecordingFrames);
    public bool HasRecording => _engine.Recording.Count >= 2;
    public string TouchRecordingName { get => _touchRecordingName; set { Set(ref _touchRecordingName, value); SaveRecordingCommand.NotifyCanExecuteChanged(); } }
    public bool CanSaveRecording => !IsRunning && HasRecording && !string.IsNullOrWhiteSpace(TouchRecordingName);
    public AsyncCommand PauseCommand { get; private set; } = null!;
    public AsyncCommand ResetStrengthCommand { get; private set; } = null!;
    public AsyncCommand SaveRecordingCommand { get; private set; } = null!;

    public PresetItem? SelectedPresetB
    {
        get => _selectedPresetB;
        set
        {
            if (value is null || !Set(ref _selectedPresetB, value)) return;
            Raise(nameof(SelectedWaveformB));
            if (IsRunning && SeparateWaveforms) _ = GuardAsync(() => ApplyChannelBAsync(value));
        }
    }
    private async Task ApplyChannelBAsync(PresetItem item)
    {
        try { await _engine.ChangeChannelBWaveformAsync(item.Preset); }
        catch
        {
            _selectedPresetB = Presets.FirstOrDefault(preset => preset.Preset.Id == _engine.CurrentWaveformB?.Id);
            Raise(nameof(SelectedPresetB)); Raise(nameof(SelectedWaveformB)); throw;
        }
    }
    private void InitializeEstimCommands()
    {
        PauseCommand = new(TogglePauseAsync, ShowError, () => IsRunning);
        ResetStrengthCommand = new(async () => { await _engine.ResetStrengthAsync(); RefreshPlayback(); }, ShowError, () => CanInteractEstim);
        SaveRecordingCommand = new(SaveRecordingAsync, ShowError, () => CanSaveRecording);
    }
    private void InitializeEstim(EstimSettings? settings)
    {
        settings ??= new(); var strength = settings.Strength ?? new();
        SelectedPlaybackOrder = PlaybackOrders.First(option => option.Value == (int)settings.Order);
        SelectedStrengthMode = StrengthModes.First(option => option.Value == (int)strength.Mode);
        SelectedTouchPlayback = TouchPlaybackModes.First(option => option.Value == (int)settings.TouchPlayback);
        SeparateWaveforms = settings.SeparateWaveforms; SelectedPresetB = Presets.First();
        GentleMode = settings.Gentle; ShowContacts = settings.ShowContacts;
        StrengthInterval = strength.IntervalSeconds; RandomAddition = strength.RandomAddition;
        StrengthStep = strength.Step; StrengthCeiling = strength.Ceiling; BoostAmount = strength.Boost;
    }
    private EstimSettings CreateEstimSettings() => new((PlaybackOrder)SelectedPlaybackOrder.Value,
        new((StrengthMode)SelectedStrengthMode.Value, StrengthInterval, RandomAddition, StrengthStep, StrengthCeiling, BoostAmount),
        SeparateWaveforms, GentleMode, ShowContacts, (TouchPlayback)SelectedTouchPlayback.Value);
    private SessionProgram CreateSessionProgram() => new(CreateEstimSettings(),
        Presets.Select(item => item.Preset).Where(preset => _controller.IsSimulation || preset.Family != WaveformFamily.Motor).ToArray(),
        !UseTouchControl && SeparateWaveforms && HasChannelB ? SelectedPresetB?.Preset : null, UseTouchControl);
    public async Task TogglePauseAsync()
    {
        try
        {
            await _engine.SetPausedAsync(!IsPaused);
            if (IsPaused || !IsRunning) _clock.Stop(); else _clock.Start();
        }
        finally { RefreshPlayback(); }
    }
    public async Task SaveRecordingAsync()
    {
        if (!CanSaveRecording) throw new LocalizedOperationException("Error.TouchRecording");
        var samples = _engine.Recording.ToArray();
        var preset = new WaveformPreset(Guid.NewGuid(), TouchRecordingName.Trim(), WaveShape.Sampled, samples.Length / 10.0,
            samples[0].FrequencyCode, Samples: samples, Family: WaveformFamily.Electrical);
        preset.Validate();
        await SaveAsync(Presets.Select(item => item.Preset).Append(preset));
        var item = new PresetItem(preset); Presets.Add(item); SelectedPreset = item;
        Notify("Status.WaveSaved", preset.Name);
    }
    private void RefreshEstimPlayback()
    {
        if (IsRunning && !UseTouchControl)
        {
            var a = Presets.FirstOrDefault(item => item.Preset.Id == _engine.CurrentWaveform?.Id);
            if (a is not null && a != _selectedPreset)
            {
                _selectedPreset = a;
                foreach (var name in new[] { nameof(SelectedPreset), nameof(SelectedPresetName), nameof(SelectedWaveform), nameof(PresetSummary) }) Raise(name);
                DeleteWaveCommand.NotifyCanExecuteChanged();
            }
            var b = Presets.FirstOrDefault(item => item.Preset.Id == _engine.CurrentWaveformB?.Id);
            if (b is not null && b != _selectedPresetB) { _selectedPresetB = b; Raise(nameof(SelectedPresetB)); Raise(nameof(SelectedWaveformB)); }
        }
        if (!CanInteractEstim)
        {
            if (_boostHeld) { _boostHeld = false; Raise(nameof(IsBoostHeld)); }
            if (_touchHeld) { _touchHeld = false; Raise(nameof(IsTouchHeld)); }
        }
        foreach (var name in new[] { nameof(IsPaused), nameof(CanInteractEstim), nameof(PauseLabel), nameof(CanOffsetChannelB), nameof(RecordingSummary), nameof(HasRecording), nameof(CanSaveRecording) }) Raise(name);
        PauseCommand.NotifyCanExecuteChanged(); ResetStrengthCommand.NotifyCanExecuteChanged(); SaveRecordingCommand.NotifyCanExecuteChanged();
    }
    private void RefreshEstimLocalization()
    {
        foreach (var option in PlaybackOrders.Concat(StrengthModes).Concat(TouchPlaybackModes)) option.RefreshLocalization();
        foreach (var name in new[] { nameof(PauseLabel), nameof(BoostLabel), nameof(TouchCoordinates), nameof(RecordingSummary), nameof(SelectedWaveformB) }) Raise(name);
    }
}

public sealed class SessionOption(int value, string key) : ObservableObject
{
    public int Value { get; } = value;
    public string Label => L10n.Get(key);
    public override string ToString() => Label;
    public void RefreshLocalization() => Raise(nameof(Label));
}
