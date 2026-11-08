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
    private double _channelA, _channelB, _outputLimit = 60, _sessionDurationSeconds = 600;
    private string _name;
    private PresetItem? _selectedPreset;
    public Guid Id
    { get; }
    public ModeKind Kind
    { get; }
    public PlaybackModeDefinition Definition
    { get; }
    public string Name
    {
        get => _name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 60)
                Set(ref _name, value);
            else
                Raise(nameof(Name));
        }
    }
    public ObservableCollection<PresetItem> Presets => _host.Presets;
    public ObservableCollection<ModeTargetItem> Targets
    { get; } = [];
    public bool HasTargets => Targets.Count > 0;
    public bool IsSelected => _host.SelectedMode == this;
    public string KindLabel => L10n.Get($"ModeKind.{Kind}");
    public ActionCommand SelectCommand
    { get; }
    public AsyncCommand PlaybackCommand
    { get; }
    internal void RefreshSelection() => Raise(nameof(IsSelected));
    public IReadOnlyCollection<string> TargetIds => _targets;
    public bool IsServerMode => Kind == ModeKind.WebSocketServer;
    public RemoteServerEditorViewModel ServerEditor { get; }
    public bool IsRunning => IsServerMode ? ServerEditor.IsRunning : _engine.IsRunning;
    public bool CanEditRouting => !IsRunning && !_starting && !IsCalibrating && !_targets.Any(id => _controller.DeviceOwner(id) == Id);
    public bool UsesWaveform(Guid id) => _engine.UsesWaveform(id) || IsRunning && SupportsPlaylist && Playlist.Any(entry => entry.Id == id);
    public bool CanStart => CanEditRouting && StartValidationError is null;
    public bool HasStartIssue => !IsRunning && StartValidationError is not null;
    public string StartIssue => !IsRunning && StartValidationError is { } key ? L10n.Get(key) : "";
    private string? StartValidationError => _targets.Count == 0 || _targets.Any(id => !_controller.Connections.ContainsKey(id))
        ? "Error.SelectConnected"
        : (UsesCustomPlaylist || IsSleep && IsRandomSleep) && !HasPlaylist ? "Error.EmptyPlaylist"
        : UsesWaveforms && !UsesCustomPlaylist && SelectedPreset is null ? "Error.SelectWave" : HardwareValidationError;
    public bool HasChannelB => _targets.Count == 0 || TargetDescriptors().Any(device => device.HasChannel(1));
    public string RoutingSummary => L10n.Format("Mode.TargetCount", _targets.Count);
    public string SessionState => L10n.Get(IsPaused ? "Session.Paused" : IsRunning ? "Session.Running" : "Session.Idle");
    public string PlaybackLabel => L10n.Get(IsRunning ? "Session.Stop" : "Session.Start");
    public string Elapsed => FormatDuration(IsServerMode ? ServerEditor.Elapsed : _engine.Elapsed);
    private TimeSpan? ConfiguredDuration => TimerEnabled ? TimeSpan.FromSeconds(SessionDurationSeconds) : null;
    public bool IsTimedSession => _hasSessionClock ? (IsServerMode ? ServerEditor.Duration : _engine.Duration).HasValue : TimerEnabled;
    public bool CanEditDuration => CanEditRouting && TimerEnabled;
    public string Remaining => (_hasSessionClock ? (IsServerMode ? ServerEditor.Remaining : _engine.Remaining) : ConfiguredDuration) is { } remaining
        ? FormatDuration(TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds))) : L10n.Get("Session.NoTimer");
    public string SessionClock => IsTimedSession ? Remaining : Elapsed;
    public string SessionTimeLabel => L10n.Get(IsTimedSession ? "Session.Remaining" : "Session.Elapsed");
    public bool ShowTimerProgress => IsRunning && IsTimedSession;
    public double TimerProgress => (IsServerMode ? ServerEditor.Duration : _engine.Duration) is { TotalSeconds: > 0 } duration
        ? Math.Clamp(((IsServerMode ? ServerEditor.Remaining : _engine.Remaining)?.TotalSeconds ?? 0) / duration.TotalSeconds, 0, 1) : 0;
    public string TimerReadout => !TimerEnabled ? L10n.Get("Session.NoTimer") : ShowTimerProgress ? Remaining : FormatDuration(TimeSpan.FromSeconds(SessionDurationSeconds));
    public double Playhead => IsRunning ? _engine.WaveformElapsed.TotalSeconds / 12 % 1 : 0;
    public string PresetSummary => SelectedPreset?.Detail ?? L10n.Get("Wave.Select");
    public string SelectedPresetName => UsesWaveforms ? SelectedPreset?.Name ?? L10n.Get("Wave.Select") : KindLabel;
    public WaveformPreset? SelectedWaveform => SelectedPreset?.Preset;
    public string SelectedOutputALabel => L10n.Format("Device.Output", Outputs().Select(output => WaveformRendering.Strength(output, 0)).DefaultIfEmpty().Max());
    public string SelectedOutputBLabel => L10n.Format("Device.Output", Outputs().Select(output => WaveformRendering.Strength(output, 1)).DefaultIfEmpty().Max());
    private IEnumerable<OutputFrame> Outputs() => _targets.Where(id => _controller.DeviceOwner(id) == Id)
        .Select(id => _controller.Connections.TryGetValue(id, out var connection) ? connection.Output : OutputFrame.Zero);
    private AsyncCommand DeleteWaveCommand => _host.DeleteWaveCommand;

    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is not null && UsesWaveforms && !AcceptsWaveform(value.Preset))
            {
                _host.ShowValidationError("Error.WaveDeviceMismatch");
                Raise(nameof(SelectedPreset));
                return;
            }
            // Replacing a filtered ItemsSource may temporarily clear ComboBox.SelectedItem.
            if (value is null && _refreshingWaveformChoices && !UsesCustomPlaylist)
                return;
            if (UsesCustomPlaylist && (value is null ? Playlist.Count > 0 : Playlist.All(entry => entry.Id != value.Preset.Id)))
            {
                Raise(nameof(SelectedPreset));
                return;
            }
            if (!Set(ref _selectedPreset, value))
                return;
            Raise(nameof(PresetSummary));
            Raise(nameof(SelectedPresetName));
            Raise(nameof(SelectedWaveform));
            Raise(nameof(CanStart));
            DeleteWaveCommand.NotifyCanExecuteChanged();
            if (IsRunning && UsesWaveforms)
                _ = GuardAsync(() => ApplySelectedWaveformAsync(value));
        }
    }
    private async Task ApplySelectedWaveformAsync(PresetItem? selected)
    {
        if (selected is null)
        {
            await StopAsync("Reason.WaveChanged", false);
            return;
        }
        try
        {
            if (await _engine.ChangeWaveformAsync(selected.Preset))
            {
                RefreshPlayback();
                Notify("Status.WaveChanged", selected);
            }
        }
        catch
        {
            if (SelectedPreset == selected)
            {
                _selectedPreset = Presets.FirstOrDefault(item => item.Preset.Id == _engine.CurrentWaveform?.Id);
                foreach (var property in new[] { nameof(SelectedPreset), nameof(SelectedPresetName), nameof(SelectedWaveform), nameof(PresetSummary) })
                    Raise(property);
            }
            throw;
        }
    }
    public double ChannelA
    {
        get => _channelA;
        set
        {
            if (double.IsFinite(value))
            {
                Set(ref _channelA, Math.Clamp(value, 0, OutputLimit));
                _engine.ChannelA = _channelA;
                RefreshNativeStrength(nameof(NativeEnabledA));
                if (value != _channelA)
                    Raise(nameof(ChannelA));
            }
        }
    }
    public double ChannelB
    {
        get => _channelB;
        set
        {
            if (double.IsFinite(value))
            {
                Set(ref _channelB, Math.Clamp(value, 0, OutputLimit));
                _engine.ChannelB = _channelB;
                RefreshNativeStrength(nameof(NativeEnabledB));
                if (value != _channelB)
                    Raise(nameof(ChannelB));
            }
        }
    }
    public double OutputLimit
    {
        get => _outputLimit;
        set
        {
            if (!double.IsFinite(value) || !Set(ref _outputLimit, Math.Clamp(value, 0, 100)))
                return;
            ChannelA = Math.Min(ChannelA, _outputLimit);
            ChannelB = Math.Min(ChannelB, _outputLimit);
            ChannelC = Math.Min(ChannelC, _outputLimit);
            _ = GuardAsync(() => _engine.SetOutputLimitAsync(_outputLimit));
        }
    }
    public bool TimerEnabled
    {
        get => _timerEnabled;
        set
        {
            if (CanEditRouting && Set(ref _timerEnabled, value))
                OnTimerSettingsChanged();
        }
    }
    public double SessionDurationSeconds
    {
        get => _sessionDurationSeconds;
        set
        {
            if (CanEditRouting && double.IsFinite(value) && Set(ref _sessionDurationSeconds, Math.Clamp(Math.Round(value), 1, MaximumDurationSeconds)))
                OnTimerSettingsChanged();
        }
    }
    public double MaximumDurationSeconds => _host.TimerLimitMinutes * 60;
    public bool OffsetChannelB
    {
        get => _offsetChannelB;
        set
        {
            Set(ref _offsetChannelB, value);
            _engine.OffsetChannelB = value;
        }
    }
    private void OnTimerSettingsChanged()
    {
        _hasSessionClock = false;
        foreach (var property in new[] { nameof(CanEditDuration), nameof(IsTimedSession), nameof(Remaining), nameof(SessionClock), nameof(SessionTimeLabel), nameof(ShowTimerProgress), nameof(TimerReadout) })
            Raise(property);
    }
    public void OnTimerLimitChanged()
    {
        if (!IsRunning)
            SessionDurationSeconds = Math.Min(SessionDurationSeconds, MaximumDurationSeconds);
        Raise(nameof(MaximumDurationSeconds));
    }

    public PlaybackModeViewModel(MainViewModel host, DeviceController controller, PlaybackModeConfig config)
    {
        _host = host;
        _controller = controller;
        Id = config.Id;
        _name = config.Name;
        Kind = config.Kind;
        Definition = PlaybackModeDefinition.For(Kind);
        _engine = new(controller, ownerId: Id);
        ServerEditor = new(Id, controller, config.Server, () => CanEditRouting, ShowError);
        NativeEditor = new(config.NativeProgram, () => CanEditRouting);
        HardwareEditor = new(config.Hardware, () => CanEditRouting);
        InitializeEstimCommands();
        SelectCommand = new(() => _host.SelectedMode = this);
        PlaybackCommand = new(TogglePlaybackAsync, ShowError, () => IsRunning || CanStart);
        _outputLimit = config.OutputLimit;
        ChannelA = config.ChannelA;
        ChannelB = config.ChannelB;
        ChannelC = config.ChannelC;
        _pump = config.Pump ?? new();
        _balances = config.DgBalances ?? new();
        TimerEnabled = config.TimerEnabled;
        SessionDurationSeconds = config.DurationSeconds;
        OffsetChannelB = config.OffsetChannelB;
        InitializeLiveModes(config.Audio, config.Reactive);
        _selectedPreset = Presets.FirstOrDefault(item => item.Preset.Id == config.WaveformAId) ?? Presets.FirstOrDefault();
        InitializeEstim(config.Estim);
        _selectedPresetB = Presets.FirstOrDefault(item => item.Preset.Id == config.WaveformBId) ?? _selectedPreset;
        InitializePlaylist(config.PlaylistWaveformIds);
        foreach (var id in config.TargetDeviceIds ?? [])
            _targets.Add(id);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CanStart))
            {
                Raise(nameof(HasStartIssue));
                Raise(nameof(StartIssue));
            }
        };
        _engine.Completed += (_, record) =>
        {
            _ = GuardAsync(ReleaseInputAsync);
            _host.AddModeSession(record with { ModeId = Id, ModeName = Name });
        };
        NativeEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NativeProgramEditorViewModel.Settings)) OnNativeSettingsChanged();
        };
        HardwareEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HardwareOutputEditorViewModel.Settings))
            {
                Raise(nameof(CanStart));
                PlaybackCommand.NotifyCanExecuteChanged();
            }
        };
        ServerEditor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RemoteServerEditorViewModel.IsRunning)) RefreshPlayback(); };
        RefreshTargets();
    }

    public PlaybackModeConfig ToConfig() => new(Id, Name, Kind, ChannelA, ChannelB, OutputLimit, TimerEnabled,
        (int)SessionDurationSeconds, SelectedPreset?.Preset.Id, SelectedPresetB?.Preset.Id, CreateEstimSettings(), _targets.ToList(), OffsetChannelB,
        Playlist.Select(item => item.Id).ToList(), IsAudio ? _audioSettings : null, IsReactive ? _reactiveSettings : null, ChannelC, IsPump ? _pump : null, _balances, IsNativeProgram ? NativeEditor.Settings : null, HardwareEditor.Settings, IsServerMode ? ServerEditor.Settings : null);
    public void AssignTarget(string id, bool selected)
    {
        if (!CanEditRouting)
            return;
        if (selected && _host.AssignedMode(id) is { } other && other != this)
            throw new LocalizedOperationException("Error.DeviceAssigned");
        if (selected && Device(id) is { } device && !CanAssignDevice(device.Device))
            throw new LocalizedOperationException("Error.ModeDeviceMismatch");
        if (!(selected ? _targets.Add(id) : _targets.Remove(id)))
            return;
        _host.RefreshModeGroups();
    }
    public void RefreshTargets()
    {
        var ids = _targets.ToArray();
        foreach (var item in Targets.Where(item => !ids.Contains(item.Id)).ToArray())
            Targets.Remove(item);
        foreach (var id in ids)
        {
            var item = Targets.FirstOrDefault(item => item.Id == id);
            if (item is null)
                Targets.Add(new(this, id));
            else
                item.Refresh();
        }
        foreach (var property in new[] { nameof(HasChannelB), nameof(RoutingSummary), nameof(SelectedOutputALabel), nameof(SelectedOutputBLabel) })
            Raise(property);
        Raise(nameof(HasTargets));
        Raise(nameof(CanStart));
        PlaybackCommand.NotifyCanExecuteChanged();
        RefreshHardware();
    }
    internal DeviceItem? Device(string id) => _host.Devices.FirstOrDefault(device => device.Id == id);
    internal async Task SelectDeviceAsync(string id)
    {
        _host.SelectedMode = this;
        if (Device(id) is { } device)
            await _host.SelectDeviceAsync(device);
    }
    internal Task ConnectDeviceAsync(string id) => Device(id) is { } device ? _host.ToggleConnectionAsync(device) : Task.CompletedTask;
    internal void ReportError(Exception error) => _host.ShowError(error);
    internal string TargetStatus(string id)
    {
        if (_controller.DeviceOwner(id) is { } owner && owner != Id)
            return L10n.Get("Mode.TargetBusy");
        return Device(id) is { } device ? L10n.Get(device.IsConnected ? "Device.Connected" : "Device.Available") : L10n.Get("Mode.TargetMissing");
    }
    public async Task TogglePlaybackAsync()
    {
        if (_starting || IsCalibrating)
            return;
        if (IsRunning)
        {
            await StopAsync();
            return;
        }
        if (StartValidationError is { } validationError)
        {
            _host.ShowValidationError(validationError);
            return;
        }
        if (IsServerMode)
        {
            _starting = true;
            RefreshPlayback();
            try
            {
                await ServerEditor.StartAsync(_targets.ToArray(), ConfiguredDuration);
                _hasSessionClock = true;
                Notify("Status.ModeStarted", Name);
            }
            finally
            {
                _starting = false;
                RefreshPlayback();
            }
            return;
        }
        var program = CreateSessionProgram();
        var waveform = !UsesWaveforms ? new WaveformPreset(Guid.NewGuid(), KindLabel, WaveShape.Pulse, 1, 20)
            : UsesCustomPlaylist
                ? program.Playlist!.First() : SelectedPreset!.Preset;
        _starting = true;
        RefreshPlayback();
        try
        {
            await StartInputAsync();
            await _engine.StartAsync(_targets, waveform, ConfiguredDuration, program);
            if (!IsRunning)
                return;
            _hasSessionClock = true;
            _clock.Restart();
            Notify("Status.ModeStarted", Name);
        }
        catch (OperationCanceledException)
        {
            await ReleaseInputAsync();
        }
        catch
        {
            await ReleaseInputAsync();
            throw;
        }
        finally
        {
            _starting = false;
            RefreshPlayback();
        }
    }
    public async Task TickAsync()
    {
        if (IsServerMode)
        {
            ServerEditor.Refresh();
            RefreshPlayback();
            return;
        }
        if (_tickBusy || !IsRunning || IsPaused)
            return;
        _tickBusy = true;
        try
        {
            await UpdateLiveInputAsync();
            await _engine.AdvanceAsync(_clock.Elapsed);
            RefreshPlayback();
        }
        catch (Exception error)
        {
            Exception reported = error;
            try
            {
                await StopAsync("Reason.InputLost", false);
            }
            catch (Exception stopError)
            {
                reported = new LocalizedAggregateException("Error.StopUnconfirmed", [error, stopError]);
            }
            _host.ShowError(reported);
            RefreshPlayback();
        }
        finally
        {
            _tickBusy = false;
        }
    }
    public async Task StopAsync(string reason = "Reason.Manual", bool notify = true)
    {
        if (IsServerMode)
        {
            await ServerEditor.StopAsync();
            RefreshPlayback();
            return;
        }
        CancelInput();
        try
        {
            await _engine.StopAsync(reason);
        }
        finally
        {
            try
            {
                await ReleaseInputAsync();
            }
            finally
            {
                _clock.Stop();
                RefreshPlayback();
            }
        }
        if (notify)
            Notify("Status.ModeStopped", Name);
    }
    private void RefreshPlayback()
    {
        RefreshEstimPlayback();
        RefreshLiveModes();
        RefreshPlaylistCommands();
        RefreshTargets();
        foreach (var property in new[] { nameof(IsRunning), nameof(CanEditRouting), nameof(CanEditDuration), nameof(IsTimedSession), nameof(SessionState),
            nameof(PlaybackLabel), nameof(Elapsed), nameof(Remaining), nameof(SessionClock), nameof(SessionTimeLabel), nameof(ShowTimerProgress), nameof(TimerProgress), nameof(TimerReadout), nameof(Playhead) })
            Raise(property);
    }
    public void RefreshLocalization()
    {
        RefreshEstimLocalization();
        RefreshLiveLocalization();
        RefreshHardwareLocalization();
        NativeEditor.Refresh();
        HardwareEditor.RefreshLocalization();
        RefreshPlaylist();
        RefreshPlayback();
        Raise(nameof(SelectedPresetName));
        Raise(nameof(KindLabel));
    }
    private void Notify(string key, params object?[] args) => _host.Notify(key, args);
    private void ShowError(Exception error) => _host.ShowError(error);
    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            _host.ShowError(error);
        }
    }
    private static string FormatDuration(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
}

public sealed class ModeTargetItem : ObservableObject
{
    private readonly PlaybackModeViewModel _mode;
    public string Id
    { get; }
    private DeviceItem? Device => _mode.Device(Id);
    public string Name => Device?.Name ?? Id;
    public string Status => _mode.TargetStatus(Id);
    public string Kind => Device?.Kind ?? L10n.Get("Mode.TargetMissing");
    public string Symbol => Device?.Symbol ?? "?";
    public bool IsSelected => Device?.IsSelected ?? false;
    public bool IsConnected => Device?.IsConnected ?? false;
    public string ConnectionAction => Device?.ConnectionAction ?? L10n.Get("Device.Connect");
    public string SensorReading => _mode.SensorReading(Id);
    public AsyncCommand SelectCommand
    { get; }
    public AsyncCommand ConnectCommand
    { get; }
    public AsyncCommand DetachCommand
    { get; }
    public ModeTargetItem(PlaybackModeViewModel mode, string id)
    {
        _mode = mode;
        Id = id;
        SelectCommand = new(() => mode.SelectDeviceAsync(id), mode.ReportError);
        ConnectCommand = new(() => mode.ConnectDeviceAsync(id), mode.ReportError, () => Device is not null);
        DetachCommand = new(() =>
        {
            mode.AssignTarget(id, false);
            return Task.CompletedTask;
        }, mode.ReportError, () => mode.CanEditRouting);
    }
    public void Refresh()
    {
        foreach (var name in new[] { nameof(Name), nameof(Status), nameof(Kind), nameof(Symbol), nameof(IsSelected), nameof(IsConnected), nameof(ConnectionAction), nameof(SensorReading) })
            Raise(name);
        ConnectCommand.NotifyCanExecuteChanged();
        DetachCommand.NotifyCanExecuteChanged();
    }
}
