using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using YokoWave.App.Playback;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class PlaybackModeViewModel
{
    private AudioSettings _audioSettings = new();
    private ReactiveSettings _reactiveSettings = new();
    private ModeInputController _input = null!;
    private bool _calibrating;
    private double _simulatedAngle;
    private static readonly SessionOption[] AudioSourceOptions = Options<AudioSourceKind>("AudioSource");
    private static readonly SessionOption[] AudioFollowOptions = Options<AudioFollowMode>("AudioFollow");
    private static readonly SessionOption[] SleepOptions = Options<SleepCycleKind>("SleepCycle");
    public IReadOnlyList<SessionOption> AudioSources => AudioSourceOptions;
    public IReadOnlyList<SessionOption> AudioFollowModes => AudioFollowOptions;
    public IReadOnlyList<SessionOption> SleepCycles => SleepOptions;
    public bool IsAudio => Definition.UsesAudio;
    public bool IsRestraint => Kind == ModeKind.Restraint;
    public bool IsExercise => Kind == ModeKind.Exercise;
    public bool IsSleep => Kind == ModeKind.SleepDeprivation;
    public bool UsesWaveforms => Definition.UsesWaveforms;
    public bool IsReactive => Definition.UsesReactiveSettings;
    public bool IsMediaFile => _audioSettings.Source == AudioSourceKind.MediaFile;
    public bool IsSystemAudio => _audioSettings.Source == AudioSourceKind.SystemAudio;
    public bool IsRandomSleep => _reactiveSettings.SleepCycle == SleepCycleKind.Random;
    public bool UsesMotion => Definition.NeedsMotion(_reactiveSettings);
    public bool ShowSimulationInput => UsesMotion && _controller.AreSimulated(_targets);
    public bool IsCalibrating => _calibrating;
    public bool HasLiveInput => IsRunning || IsCalibrating;
    public string CurrentContentTitle => L10n.Get(UsesWaveforms ? "Wave.Current" : "Mode.Current");
    public bool CanCalibrate => IsAudio && !IsMediaFile && CanEditRouting;
    public SessionOption SelectedAudioSource
    {
        get => AudioSources.First(option => option.Value == (int)_audioSettings.Source);
        set { if (value is not null) ChangeAudio(settings => settings with { Source = (AudioSourceKind)value.Value }); }
    }
    public SessionOption SelectedAudioFollow
    {
        get => AudioFollowModes.First(option => option.Value == (int)_audioSettings.Follow);
        set { if (value is not null) ChangeAudio(settings => settings with { Follow = (AudioFollowMode)value.Value }); }
    }
    public SessionOption SelectedSleepCycle
    {
        get => SleepCycles.First(option => option.Value == (int)_reactiveSettings.SleepCycle);
        set { if (value is not null) ChangeReactive(settings => settings with { SleepCycle = (SleepCycleKind)value.Value }); }
    }
    public string MediaPath { get => _audioSettings.MediaPath ?? ""; set => ChangeAudio(settings => settings with { MediaPath = value }); }
    public double AudioSensitivity { get => _audioSettings.Sensitivity; set { if (double.IsFinite(value)) ChangeAudio(settings => settings with { Sensitivity = Math.Clamp(value, 0, 100) }); } }
    public double NoiseFloorDb => _audioSettings.NoiseFloorDb;
    public string NoiseFloorLabel => L10n.Format("Audio.NoiseFloorFormat", NoiseFloorDb);
    public double PlaybackVolume { get => _audioSettings.PlaybackVolume; set { if (double.IsFinite(value)) ChangeAudio(settings => settings with { PlaybackVolume = Math.Clamp(value, 0, 100) }); } }
    public bool LoopMedia { get => _audioSettings.LoopMedia; set => ChangeAudio(settings => settings with { LoopMedia = value }); }
    public double SafeAngle { get => _reactiveSettings.SafeAngle; set => ReactiveNumber(value, 0, 90, (s, v) => s with { SafeAngle = v }); }
    public double RampAngle { get => _reactiveSettings.RampAngle; set => ReactiveNumber(value, 1, 180, (s, v) => s with { RampAngle = v }); }
    public double MovementSensitivity { get => _reactiveSettings.MovementSensitivity; set => ReactiveNumber(value, 0, 100, (s, v) => s with { MovementSensitivity = v }); }
    public double IdleSeconds { get => _reactiveSettings.IdleSeconds; set => ReactiveNumber(value, 1, 3600, (s, v) => s with { IdleSeconds = v }); }
    public double BurstSeconds { get => _reactiveSettings.BurstSeconds; set => ReactiveNumber(value, 1, 60, (s, v) => s with { BurstSeconds = v }); }
    public bool MovementGoalEnabled { get => _reactiveSettings.MovementGoalEnabled; set => ChangeReactive(settings => settings with { MovementGoalEnabled = value }); }
    public double MovementGoal { get => _reactiveSettings.MovementGoal; set => ReactiveNumber(value, 1, 1000000, (s, v) => s with { MovementGoal = (int)Math.Round(v) }); }
    public double IntervalSeconds { get => _reactiveSettings.IntervalSeconds; set => ReactiveNumber(value, 1, 7200, (s, v) => s with { IntervalSeconds = v }); }
    public double MinimumIntervalSeconds { get => _reactiveSettings.MinimumIntervalSeconds; set => ReactiveNumber(value, 1, 7200, (s, v) => s with { MinimumIntervalSeconds = v, MaximumIntervalSeconds = Math.Max(v, s.MaximumIntervalSeconds) }); }
    public double MaximumIntervalSeconds { get => _reactiveSettings.MaximumIntervalSeconds; set => ReactiveNumber(value, 1, 7200, (s, v) => s with { MaximumIntervalSeconds = Math.Max(v, s.MinimumIntervalSeconds) }); }
    public double MinimumBurstSeconds { get => _reactiveSettings.MinimumBurstSeconds; set => ReactiveNumber(value, 1, 60, (s, v) => s with { MinimumBurstSeconds = v, MaximumBurstSeconds = Math.Max(v, s.MaximumBurstSeconds) }); }
    public double MaximumBurstSeconds { get => _reactiveSettings.MaximumBurstSeconds; set => ReactiveNumber(value, 1, 60, (s, v) => s with { MaximumBurstSeconds = Math.Max(v, s.MinimumBurstSeconds) }); }
    public double MinimumStrengthPercent { get => _reactiveSettings.MinimumStrengthPercent; set => ReactiveNumber(value, 0, 100, (s, v) => s with { MinimumStrengthPercent = v, MaximumStrengthPercent = Math.Max(v, s.MaximumStrengthPercent) }); }
    public double MaximumStrengthPercent { get => _reactiveSettings.MaximumStrengthPercent; set => ReactiveNumber(value, 0, 100, (s, v) => s with { MaximumStrengthPercent = Math.Max(v, s.MinimumStrengthPercent) }); }
    public double SimulatedAngle { get => _simulatedAngle; set { if (double.IsFinite(value)) Set(ref _simulatedAngle, Math.Clamp(value, 0, 180)); } }
    public int MovementCount => _engine.MovementCount;
    public string MovementCountLabel => L10n.Format("Exercise.Count", MovementCount);
    public string InputPhaseLabel => IsCalibrating ? L10n.Get("Audio.Calibrating") : !IsRunning || IsPaused ? SessionState :
        L10n.Get($"InputPhase.{_engine.InputStatus.Values.FirstOrDefault()?.Phase ?? InputPhase.Waiting}");
    public string AudioReadout => AudioInput?.Ended == true ? L10n.Get("Reason.MediaEnded")
        : CurrentAudioFrame is { } frame ? L10n.Format("Audio.Level", frame.LeftDb, frame.RightDb) : L10n.Get("Input.Waiting");
    private AudioModeInputController? AudioInput => _input as AudioModeInputController;
    private AudioFeatureFrame? CurrentAudioFrame => AudioInput?.CurrentFrame;
    public double InputLevel => CanInteractEstim ? _engine.InputStatus.Values.Select(status => status.Level).DefaultIfEmpty().Max() : 0;
    public string ReactiveCountdown => IsRunning && _engine.InputStatus.Values.FirstOrDefault() is { } status
        ? FormatDuration(TimeSpan.FromSeconds(Math.Ceiling(status.RemainingSeconds))) : "—";
    public AsyncCommand CalibrateAudioCommand { get; private set; }
    public AsyncCommand RecalibrateMotionCommand { get; private set; }

    [MemberNotNull(nameof(CalibrateAudioCommand), nameof(RecalibrateMotionCommand))]
    private void InitializeLiveModes(AudioSettings? audio, ReactiveSettings? reactive)
    {
        _audioSettings = audio ?? new(); _reactiveSettings = reactive ?? new();
        _input = ModeInputController.Create(Definition, _host.AudioInputs, _controller, Id,
            () => _audioSettings, () => _reactiveSettings, () => SimulatedAngle, () => (TouchFrequency, TouchPulseWidth, IsTouchHeld));
        CalibrateAudioCommand = new(CalibrateAudioAsync, ShowError, () => CanCalibrate);
        RecalibrateMotionCommand = new(_engine.RecalibrateMotionAsync, ShowError, () => IsRestraint && CanInteractEstim);
    }
    private void ChangeAudio(Func<AudioSettings, AudioSettings> change, [CallerMemberName] string? property = null)
    {
        if (!CanEditRouting) return;
        var next = change(_audioSettings); next.Validate(); _audioSettings = next;
        Raise(property); Raise(nameof(IsMediaFile)); Raise(nameof(IsSystemAudio)); Raise(nameof(CanCalibrate));
        CalibrateAudioCommand?.NotifyCanExecuteChanged();
    }
    private void ReactiveNumber(double value, double min, double max, Func<ReactiveSettings, double, ReactiveSettings> change,
        [CallerMemberName] string? property = null)
    {
        if (double.IsFinite(value)) ChangeReactive(settings => change(settings, Math.Clamp(value, min, max)), property);
    }
    private void ChangeReactive(Func<ReactiveSettings, ReactiveSettings> change, [CallerMemberName] string? property = null)
    {
        if (!CanEditRouting) return;
        var next = change(_reactiveSettings); next.Validate(); _reactiveSettings = next;
        Raise(property); Raise(nameof(CanStart));
        _host.RefreshModeGroups();
        foreach (var name in new[] { nameof(IsRandomSleep), nameof(UsesMotion), nameof(ShowSimulationInput), nameof(MaximumIntervalSeconds), nameof(MaximumBurstSeconds), nameof(MaximumStrengthPercent) }) Raise(name);
    }
    private Task StartInputAsync() => _input.StartAsync();
    private void CancelInput() => _input.Cancel();
    private Task ReleaseInputAsync() => _input.DisposeAsync().AsTask();
    private Task UpdateLiveInputAsync() => _input.UpdateAsync(_engine);
    private async Task CalibrateAudioAsync()
    {
        if (!CanCalibrate) return;
        _calibrating = true; RefreshPlayback();
        try
        {
            var floor = await AudioInput!.CalibrateAsync();
            _audioSettings = _audioSettings with { NoiseFloorDb = floor };
            Raise(nameof(NoiseFloorDb)); Notify("Status.AudioCalibrated", NoiseFloorDb);
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { await ReleaseInputAsync(); }
            finally { _calibrating = false; RefreshPlayback(); }
        }
    }
    internal string SensorReading(string id)
    {
        if (!IsRunning || IsPaused) return SessionState;
        if (!_engine.InputStatus.TryGetValue(id, out var status)) return L10n.Get("Input.Waiting");
        var angle = status.Angle?.ToString("0.0", L10n.Current.Culture) ?? "—";
        return L10n.Format(IsRestraint ? "Input.RestraintReading" : "Input.ActivityReading", L10n.Get($"InputPhase.{status.Phase}"), angle, status.Deviation, status.Movements);
    }
    private void RefreshLiveModes()
    {
        foreach (var name in new[] { nameof(AudioReadout), nameof(InputLevel), nameof(InputPhaseLabel), nameof(ReactiveCountdown), nameof(MovementCount), nameof(MovementCountLabel), nameof(NoiseFloorLabel), nameof(CanCalibrate), nameof(IsCalibrating), nameof(HasLiveInput), nameof(CurrentContentTitle) }) Raise(name);
        CalibrateAudioCommand?.NotifyCanExecuteChanged(); RecalibrateMotionCommand?.NotifyCanExecuteChanged();
    }
    private void RefreshLiveLocalization()
    {
        foreach (var option in AudioSources.Concat(AudioFollowModes).Concat(SleepCycles)) option.RefreshLocalization();
        foreach (var name in new[] { nameof(IsAudio), nameof(IsReactive), nameof(IsRestraint), nameof(IsExercise), nameof(IsSleep), nameof(ShowSimulationInput) }) Raise(name);
    }
}
