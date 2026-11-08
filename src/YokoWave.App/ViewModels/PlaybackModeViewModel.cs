using System.Collections.ObjectModel;
using System.Diagnostics;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class PlaybackModeViewModel : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly DeviceController _controller;
    private readonly SessionEngine _engine;
    private readonly Stopwatch _clock = new();
    private readonly HashSet<string> _targets = [];
    private bool _tickBusy, _starting, _hasSessionClock, _timerEnabled = true, _offsetChannelB = true;
    private double _channelA, _channelB, _safetyLimit = 60, _sessionDurationSeconds = 600;
    private string _name;
    private PresetItem? _selectedPreset;
    public Guid Id { get; }
    public string Name
    {
        get => _name;
        set { if (!string.IsNullOrWhiteSpace(value) && value.Length <= 60) Set(ref _name, value); else Raise(nameof(Name)); }
    }
    public ObservableCollection<PresetItem> Presets => _host.Presets;
    public ObservableCollection<ModeTargetItem> Targets { get; } = [];
    public IReadOnlyCollection<string> TargetIds => _targets;
    public bool IsRunning => _engine.IsRunning;
    public bool CanEditRouting => !IsRunning && !_starting;
    public bool UsesWaveform(Guid id) => _engine.UsesWaveform(id);
    public bool SelectedConnected => _targets.Any(id => _controller.Connections.ContainsKey(id));
    public bool HasChannelB => _targets.Count == 0 || _targets.Any(id => _controller.Connections.TryGetValue(id, out var connection) && connection.Device.Kind == DeviceKind.DualChannel);
    public bool HasElectrodeReadings => _host.IsBluetooth && _host.SelectedConnected && ShowContacts;
    public string RoutingSummary => L10n.Format("Mode.TargetCount", _targets.Count);
    public string SessionState => L10n.Get(IsPaused ? "Session.Paused" : IsRunning ? "Session.Running" : "Session.Idle");
    public string PlaybackLabel => L10n.Get(IsRunning ? "Session.Stop" : "Session.Start");
    public string Elapsed => FormatDuration(_engine.Elapsed);
    private TimeSpan? ConfiguredDuration => TimerEnabled ? TimeSpan.FromSeconds(SessionDurationSeconds) : null;
    public bool IsTimedSession => _hasSessionClock ? _engine.Duration.HasValue : TimerEnabled;
    public bool CanEditDuration => CanEditRouting && TimerEnabled;
    public string Remaining => (_hasSessionClock ? _engine.Remaining : ConfiguredDuration) is { } remaining
        ? FormatDuration(TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds))) : L10n.Get("Session.NoTimer");
    public string SessionClock => IsTimedSession ? Remaining : Elapsed;
    public string SessionTimeLabel => L10n.Get(IsTimedSession ? "Session.Remaining" : "Session.Elapsed");
    public bool ShowTimerProgress => IsRunning && IsTimedSession;
    public double TimerProgress => _engine.Duration is { TotalSeconds: > 0 } duration
        ? Math.Clamp((_engine.Remaining?.TotalSeconds ?? 0) / duration.TotalSeconds, 0, 1) : 0;
    public string TimerReadout => !TimerEnabled ? L10n.Get("Session.NoTimer") : ShowTimerProgress ? Remaining : FormatDuration(TimeSpan.FromSeconds(SessionDurationSeconds));
    public double Playhead => IsRunning ? _engine.WaveformElapsed.TotalSeconds / 12 % 1 : 0;
    public string PresetSummary => SelectedPreset?.Detail ?? L10n.Get("Wave.Select");
    public string SelectedPresetName => UseTouchControl ? L10n.Get("Touch.Title") : SelectedPreset?.Name ?? L10n.Get("Wave.Select");
    public WaveformPreset? SelectedWaveform => SelectedPreset?.Preset;
    public string SelectedOutputALabel => L10n.Format("Device.Output", Outputs().Select(output => output.ChannelA).DefaultIfEmpty().Max());
    public string SelectedOutputBLabel => L10n.Format("Device.Output", Outputs().Select(output => output.ChannelB).DefaultIfEmpty().Max());
    private IEnumerable<OutputFrame> Outputs() => _targets.Where(id => _controller.DeviceOwner(id) == Id)
        .Select(id => _controller.Connections.TryGetValue(id, out var connection) ? connection.Output : OutputFrame.Zero);
    private AsyncCommand DeleteWaveCommand => _host.DeleteWaveCommand;

    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value)) return;
            Raise(nameof(PresetSummary)); Raise(nameof(SelectedPresetName)); Raise(nameof(SelectedWaveform)); DeleteWaveCommand.NotifyCanExecuteChanged();
            if (IsRunning) _ = GuardAsync(() => ApplySelectedWaveformAsync(value));
        }
    }
    private async Task ApplySelectedWaveformAsync(PresetItem? selected)
    {
        if (selected is null) { await StopAsync("Reason.WaveChanged", false); return; }
        try { if (await _engine.ChangeWaveformAsync(selected.Preset)) { RefreshPlayback(); Notify("Status.WaveChanged", selected); } }
        catch
        {
            if (SelectedPreset == selected)
            {
                _selectedPreset = Presets.FirstOrDefault(item => item.Preset.Id == _engine.CurrentWaveform?.Id);
                foreach (var property in new[] { nameof(SelectedPreset), nameof(SelectedPresetName), nameof(SelectedWaveform), nameof(PresetSummary) }) Raise(property);
            }
            throw;
        }
    }
    public double ChannelA { get => _channelA; set { if (double.IsFinite(value)) { Set(ref _channelA, Math.Clamp(value, 0, SafetyLimit)); _engine.ChannelA = _channelA; if (value != _channelA) Raise(nameof(ChannelA)); } } }
    public double ChannelB { get => _channelB; set { if (double.IsFinite(value)) { Set(ref _channelB, Math.Clamp(value, 0, SafetyLimit)); _engine.ChannelB = _channelB; if (value != _channelB) Raise(nameof(ChannelB)); } } }
    public double SafetyLimit
    {
        get => _safetyLimit;
        set
        {
            if (!double.IsFinite(value) || !Set(ref _safetyLimit, Math.Clamp(value, 0, 100))) return;
            ChannelA = Math.Min(ChannelA, _safetyLimit); ChannelB = Math.Min(ChannelB, _safetyLimit);
            _ = GuardAsync(() => _engine.SetOutputLimitAsync(_safetyLimit));
        }
    }
    public bool TimerEnabled { get => _timerEnabled; set { if (CanEditRouting && Set(ref _timerEnabled, value)) OnTimerSettingsChanged(); } }
    public double SessionDurationSeconds
    {
        get => _sessionDurationSeconds;
        set { if (CanEditRouting && double.IsFinite(value) && Set(ref _sessionDurationSeconds, Math.Clamp(Math.Round(value), 1, MaximumDurationSeconds))) OnTimerSettingsChanged(); }
    }
    public double MaximumDurationSeconds => _host.TimerLimitMinutes * 60;
    public bool OffsetChannelB { get => _offsetChannelB; set { Set(ref _offsetChannelB, value); _engine.OffsetChannelB = value; } }
    private void OnTimerSettingsChanged()
    {
        _hasSessionClock = false;
        foreach (var property in new[] { nameof(CanEditDuration), nameof(IsTimedSession), nameof(Remaining), nameof(SessionClock), nameof(SessionTimeLabel), nameof(ShowTimerProgress), nameof(TimerReadout) }) Raise(property);
    }
    public void OnTimerLimitChanged() { if (!IsRunning) SessionDurationSeconds = Math.Min(SessionDurationSeconds, MaximumDurationSeconds); Raise(nameof(MaximumDurationSeconds)); }

    public PlaybackModeViewModel(MainViewModel host, DeviceController controller, PlaybackModeConfig config)
    {
        _host = host; _controller = controller; Id = config.Id; _name = config.Name;
        _engine = new(controller, ownerId: Id); InitializeEstimCommands();
        _safetyLimit = config.OutputLimit; ChannelA = config.ChannelA; ChannelB = config.ChannelB;
        TimerEnabled = config.TimerEnabled; SessionDurationSeconds = config.DurationSeconds; OffsetChannelB = config.OffsetChannelB;
        _useTouchControl = config.TouchControl;
        _selectedPreset = Presets.FirstOrDefault(item => item.Preset.Id == config.WaveformAId) ?? Presets.FirstOrDefault();
        InitializeEstim(config.Estim);
        _selectedPresetB = Presets.FirstOrDefault(item => item.Preset.Id == config.WaveformBId) ?? _selectedPreset;
        foreach (var id in config.TargetDeviceIds ?? []) _targets.Add(id);
        _engine.Completed += (_, record) => _host.AddModeSession(record with { ModeId = Id, ModeName = Name });
        RefreshTargets();
    }

    public PlaybackModeConfig ToConfig() => new(Id, Name, UseTouchControl, ChannelA, ChannelB, SafetyLimit, TimerEnabled,
        (int)SessionDurationSeconds, SelectedPreset?.Preset.Id, SelectedPresetB?.Preset.Id, CreateEstimSettings(), _targets.ToList(), OffsetChannelB);
    public void AssignTarget(string id, bool selected)
    {
        if (!CanEditRouting) return;
        if (!(selected ? _targets.Add(id) : _targets.Remove(id))) return;
        RefreshTargets();
    }
    public void RefreshTargets()
    {
        var ids = _host.Devices.Select(device => device.Id).Concat(_targets).Distinct().ToArray();
        foreach (var item in Targets.Where(item => !ids.Contains(item.Id)).ToArray()) Targets.Remove(item);
        foreach (var id in ids)
        {
            var item = Targets.FirstOrDefault(item => item.Id == id);
            if (item is null) Targets.Add(new(this, id)); else item.Refresh();
        }
        foreach (var property in new[] { nameof(SelectedConnected), nameof(HasChannelB), nameof(RoutingSummary), nameof(SelectedOutputALabel), nameof(SelectedOutputBLabel) }) Raise(property);
    }
    internal DeviceItem? Device(string id) => _host.Devices.FirstOrDefault(device => device.Id == id);
    internal string TargetStatus(string id)
    {
        if (_controller.DeviceOwner(id) is { } owner && owner != Id) return L10n.Get("Mode.TargetBusy");
        return Device(id)?.Status ?? L10n.Get("Mode.TargetMissing");
    }
    public async Task TogglePlaybackAsync()
    {
        if (_starting) return;
        if (IsRunning) { await StopAsync(); return; }
        if (SelectedPreset is null) throw new LocalizedOperationException("Error.SelectWave");
        var waveform = UseTouchControl ? Presets.First(item => item.Preset.Family == WaveformFamily.Electrical).Preset : SelectedPreset.Preset;
        _starting = true; RefreshPlayback();
        try
        {
            await _engine.StartAsync(_targets, waveform, ConfiguredDuration, CreateSessionProgram());
            if (!IsRunning) return;
            _hasSessionClock = true; _clock.Restart(); Notify("Status.ModeStarted", Name);
        }
        finally { _starting = false; RefreshPlayback(); }
    }
    public async Task TickAsync()
    {
        if (_tickBusy || !IsRunning || IsPaused) return;
        _tickBusy = true;
        try
        {
            if (UseTouchControl && IsTouchHeld) await _engine.SetTouchAsync(TouchFrequency, TouchPulseWidth, true);
            await _engine.AdvanceAsync(_clock.Elapsed); RefreshPlayback();
        }
        catch (Exception error) { _host.ShowError(error); RefreshPlayback(); }
        finally { _tickBusy = false; }
    }
    public async Task StopAsync(string reason = "Reason.Manual", bool notify = true)
    {
        try { await _engine.StopAsync(reason); }
        finally { _clock.Stop(); RefreshPlayback(); }
        if (notify) Notify("Status.ModeStopped", Name);
    }
    private void RefreshPlayback()
    {
        RefreshEstimPlayback(); RefreshTargets();
        foreach (var property in new[] { nameof(IsRunning), nameof(CanEditRouting), nameof(CanEditDuration), nameof(IsTimedSession), nameof(SessionState),
            nameof(PlaybackLabel), nameof(Elapsed), nameof(Remaining), nameof(SessionClock), nameof(SessionTimeLabel), nameof(ShowTimerProgress), nameof(TimerProgress), nameof(TimerReadout), nameof(Playhead) }) Raise(property);
    }
    public void RefreshLocalization() { RefreshEstimLocalization(); RefreshPlayback(); Raise(nameof(SelectedPresetName)); }
    private void Notify(string key, params object?[] args) => _host.Notify(key, args);
    private void ShowError(Exception error) => _host.ShowError(error);
    private async Task GuardAsync(Func<Task> action) { try { await action(); } catch (Exception error) { _host.ShowError(error); } }
    private static string FormatDuration(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
}

public sealed class ModeTargetItem(PlaybackModeViewModel mode, string id) : ObservableObject
{
    public string Id { get; } = id;
    public string Name => mode.Device(Id)?.Name ?? Id;
    public string Status => mode.TargetStatus(Id);
    public bool IsSelected { get => mode.TargetIds.Contains(Id); set => mode.AssignTarget(Id, value); }
    public bool CanEdit => mode.CanEditRouting;
    public void Refresh() { foreach (var name in new[] { nameof(Name), nameof(Status), nameof(IsSelected), nameof(CanEdit) }) Raise(name); }
}
