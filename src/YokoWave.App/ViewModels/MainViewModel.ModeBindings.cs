using YokoWave.Core;
namespace YokoWave.App.ViewModels;

public sealed partial class MainViewModel
{
    public double ChannelA { get => SelectedMode.ChannelA; set => SelectedMode.ChannelA = value; }
    public double ChannelB { get => SelectedMode.ChannelB; set => SelectedMode.ChannelB = value; }
    public double SafetyLimit { get => SelectedMode.SafetyLimit; set => SelectedMode.SafetyLimit = value; }
    public double SessionDurationSeconds { get => SelectedMode.SessionDurationSeconds; set => SelectedMode.SessionDurationSeconds = value; }
    public double StrengthInterval { get => SelectedMode.StrengthInterval; set => SelectedMode.StrengthInterval = value; }
    public double RandomAddition { get => SelectedMode.RandomAddition; set => SelectedMode.RandomAddition = value; }
    public double StrengthStep { get => SelectedMode.StrengthStep; set => SelectedMode.StrengthStep = value; }
    public double StrengthCeiling { get => SelectedMode.StrengthCeiling; set => SelectedMode.StrengthCeiling = value; }
    public double BoostAmount { get => SelectedMode.BoostAmount; set => SelectedMode.BoostAmount = value; }
    public double TouchFrequency { get => SelectedMode.TouchFrequency; set => SelectedMode.TouchFrequency = value; }
    public double TouchPulseWidth { get => SelectedMode.TouchPulseWidth; set => SelectedMode.TouchPulseWidth = value; }
    public bool TimerEnabled { get => SelectedMode.TimerEnabled; set => SelectedMode.TimerEnabled = value; }
    public bool OffsetChannelB { get => SelectedMode.OffsetChannelB; set => SelectedMode.OffsetChannelB = value; }
    public bool SeparateWaveforms { get => SelectedMode.SeparateWaveforms; set => SelectedMode.SeparateWaveforms = value; }
    public bool GentleMode { get => SelectedMode.GentleMode; set => SelectedMode.GentleMode = value; }
    public bool ShowContacts { get => SelectedMode.ShowContacts; set => SelectedMode.ShowContacts = value; }
    public bool UseTouchControl { get => SelectedMode.UseTouchControl; set => SelectedMode.UseTouchControl = value; }
    public bool IsBoostHeld { get => SelectedMode.IsBoostHeld; set => SelectedMode.IsBoostHeld = value; }
    public bool IsTouchHeld { get => SelectedMode.IsTouchHeld; set => SelectedMode.IsTouchHeld = value; }
    public bool ShowStrengthOptions { get => SelectedMode.ShowStrengthOptions; set => SelectedMode.ShowStrengthOptions = value; }
    public string TouchRecordingName { get => SelectedMode.TouchRecordingName; set => SelectedMode.TouchRecordingName = value; }
    public PresetItem? SelectedPreset { get => SelectedMode.SelectedPreset; set => SelectedMode.SelectedPreset = value; }
    public PresetItem? SelectedPresetB { get => SelectedMode.SelectedPresetB; set => SelectedMode.SelectedPresetB = value; }
    public SessionOption SelectedPlaybackOrder { get => SelectedMode.SelectedPlaybackOrder; set => SelectedMode.SelectedPlaybackOrder = value; }
    public SessionOption SelectedStrengthMode { get => SelectedMode.SelectedStrengthMode; set => SelectedMode.SelectedStrengthMode = value; }
    public SessionOption SelectedTouchPlayback { get => SelectedMode.SelectedTouchPlayback; set => SelectedMode.SelectedTouchPlayback = value; }
    public bool IsRunning => SelectedMode.IsRunning;
    public bool CanEditRouting => SelectedMode.CanEditRouting;
    public bool HasChannelB => SelectedMode.HasChannelB;
    public bool IsTimedSession => SelectedMode.IsTimedSession;
    public bool CanEditDuration => SelectedMode.CanEditDuration;
    public bool ShowTimerProgress => SelectedMode.ShowTimerProgress;
    public bool CanOffsetChannelB => SelectedMode.CanOffsetChannelB;
    public bool IsRandomStrength => SelectedMode.IsRandomStrength;
    public bool IsStepStrength => SelectedMode.IsStepStrength;
    public bool IsAutomatedStrength => SelectedMode.IsAutomatedStrength;
    public bool IsPaused => SelectedMode.IsPaused;
    public bool CanInteractEstim => SelectedMode.CanInteractEstim;
    public bool IsWaveformControl => SelectedMode.IsWaveformControl;
    public bool HasRecording => SelectedMode.HasRecording;
    public bool CanSaveRecording => SelectedMode.CanSaveRecording;
    public double TimerProgress => SelectedMode.TimerProgress;
    public double Playhead => SelectedMode.Playhead;
    public double MaximumDurationSeconds => SelectedMode.MaximumDurationSeconds;
    public string SessionState => SelectedMode.SessionState;
    public string PlaybackLabel => SelectedMode.PlaybackLabel;
    public string Elapsed => SelectedMode.Elapsed;
    public string Remaining => SelectedMode.Remaining;
    public string SessionClock => SelectedMode.SessionClock;
    public string SessionTimeLabel => SelectedMode.SessionTimeLabel;
    public string TimerReadout => SelectedMode.TimerReadout;
    public string RoutingSummary => SelectedMode.RoutingSummary;
    public string PresetSummary => SelectedMode.PresetSummary;
    public string SelectedPresetName => SelectedMode.SelectedPresetName;
    public string SelectedOutputALabel => SelectedMode.SelectedOutputALabel;
    public string SelectedOutputBLabel => SelectedMode.SelectedOutputBLabel;
    public string PauseLabel => SelectedMode.PauseLabel;
    public string BoostLabel => SelectedMode.BoostLabel;
    public string TouchCoordinates => SelectedMode.TouchCoordinates;
    public string RecordingSummary => SelectedMode.RecordingSummary;
    public string StrengthOptionsSymbol => SelectedMode.StrengthOptionsSymbol;
    public WaveformPreset? SelectedWaveform => SelectedMode.SelectedWaveform;
    public WaveformPreset? SelectedWaveformB => SelectedMode.SelectedWaveformB;
    public AsyncCommand PauseCommand => SelectedMode.PauseCommand;
    public AsyncCommand ResetStrengthCommand => SelectedMode.ResetStrengthCommand;
    public AsyncCommand SaveRecordingCommand => SelectedMode.SaveRecordingCommand;
    public IReadOnlyList<SessionOption> PlaybackOrders => SelectedMode.PlaybackOrders;
    public IReadOnlyList<SessionOption> StrengthModes => SelectedMode.StrengthModes;
    public IReadOnlyList<SessionOption> TouchPlaybackModes => SelectedMode.TouchPlaybackModes;
    public Task TogglePauseAsync() => SelectedMode.TogglePauseAsync();
    public Task SaveRecordingAsync() => SelectedMode.SaveRecordingAsync();
}
