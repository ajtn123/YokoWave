using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly WorkspaceStore _store;
    private readonly Func<IDeviceTransport> _bluetoothFactory;
    internal IAudioInputFactory AudioInputs { get; }
    private readonly SynchronizationContext? _uiContext;
    private bool _changingTransport, _scanBusy, _disposed, _settingsLoaded;
    private string _page = "workspace";
    private LocalizedMessage _status = new("Status.NoDevice", []);
    private bool _isError;
    private LanguageOption? _selectedLanguage;
    private TransportOption? _selectedTransport;
    private DeviceItem? _selectedDevice;
    private PlaybackModeViewModel? _selectedMode;
    private PresetItem? _librarySelectedPreset;
    private double _defaultLimit = 60, _timerLimitMinutes = 120;
    private string _transferJson = "";
    public WaveformEditorViewModel WaveEditor { get; } = new();
    private readonly List<SessionRecord> _records = [];
    private SessionStatistics _statistics = new();
    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public ObservableCollection<DeviceItem> UnassignedDevices { get; } = [];
    public ObservableCollection<PresetItem> Presets { get; } = [];
    public PresetItem? LibrarySelectedPreset
    {
        get => _librarySelectedPreset ?? Presets.FirstOrDefault();
        set
        {
            Set(ref _librarySelectedPreset, value);
            DeleteWaveCommand.NotifyCanExecuteChanged(); (UseWaveCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        }
    }
    private bool CanUseLibraryWave => SelectedMode.IsWaveformControl && LibrarySelectedPreset is { } preset &&
        SelectedMode.AcceptsWaveform(preset.Preset) &&
        (!SelectedMode.UsesCustomPlaylist || SelectedMode.Playlist.Any(entry => entry.Id == preset.Preset.Id));
    public ObservableCollection<PlaybackModeViewModel> Modes { get; } = [];
    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<ActivityItem> Activities { get; } = [];
    public IReadOnlyList<ShapeItem> Shapes => WaveEditor.Shapes;
    public IReadOnlyList<LanguageOption> Languages { get; } = [new("en", L10n.Current.English["Language.en"]), new("zh-CN", L10n.Current.English["Language.zh-CN"])];
    public IReadOnlyList<TransportOption> TransportOptions { get; } = [new(false), new(true)];
    public IReadOnlyList<SessionOption> ModeTypes { get; } = Enum.GetValues<ModeKind>().Select(kind => new SessionOption((int)kind, $"ModeKind.{kind}")).ToArray();
    private SessionOption? _newModeType;
    public SessionOption NewModeType { get => _newModeType ?? ModeTypes[0]; set { if (value is not null) Set(ref _newModeType, value); } }
    public PlaybackModeViewModel SelectedMode
    {
        get => _selectedMode ?? Modes[0];
        set
        {
            if (value is null || !Modes.Contains(value) || value == _selectedMode) return;
            if (_selectedMode is { } previous) { previous.IsBoostHeld = false; previous.IsTouchHeld = false; }
            Set(ref _selectedMode, value);
            foreach (var mode in Modes) mode.RefreshSelection();
            RefreshModeGroups();
            RefreshModeBindings();
        }
    }
    public bool IsAnyRunning => Modes.Any(mode => !mode.CanEditRouting);
    public string RunningModesSummary => L10n.Format("Mode.RunningCount", Modes.Count(mode => mode.IsRunning));
    public bool HasUnassignedDevices => UnassignedDevices.Count > 0;
    public double DefaultLimit { get => _defaultLimit; set { if (double.IsFinite(value)) Set(ref _defaultLimit, Math.Clamp(value, 0, 100)); } }
    public double TimerLimitMinutes
    {
        get => _timerLimitMinutes;
        set
        {
            if (IsAnyRunning || !double.IsFinite(value) || !Set(ref _timerLimitMinutes, Math.Clamp(Math.Truncate(value), 1, 120))) return;
            foreach (var mode in Modes) mode.OnTimerLimitChanged();
        }
    }
    public TransportOption? SelectedTransport { get => _selectedTransport; set { if (value is not null) Set(ref _selectedTransport, value); } }
    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set { if (value is null || !Set(ref _selectedLanguage, value)) return; L10n.Current.SetLanguage(value.Code); if (_settingsLoaded) _ = GuardAsync(() => SaveAsync()); }
    }
    public bool IsBluetooth => !_controller.IsSimulation;
    public string TransportName => _controller.TransportName;
    public string ModeLabel => $"●  {TransportName}";
    public string DriverLabel => L10n.Format("Settings.DriverFormat", TransportName);
    public string DeviceHelp => L10n.Get(IsBluetooth ? "Device.BluetoothHelp" : "Device.SimulationHelp");
    public string TransportHelp => L10n.Get(IsBluetooth ? "Settings.BluetoothHelp" : "Settings.SimulationHelp");
    public string ReadingsLabel => L10n.Get(IsBluetooth ? "Device.HardwareReadings" : "Device.Readings");
    public string StoragePath => _store.DirectoryPath;
    public long SessionCount => _statistics.SessionCount;
    public string TotalTime => L10n.Format("History.TotalFormat", _statistics.TotalDurationSeconds / 60);
    public bool IsWorkspace => _page == "workspace";
    public bool IsWaves => _page == "waves";
    public bool IsHistory => _page == "history";
    public bool IsSettings => _page == "settings";
    public bool IsHelp => _page == "help";
    public string PageTitle => L10n.Get(_page switch { "waves" => "Page.Waves", "history" => "Page.History", "settings" => "Page.Settings", "help" => "Page.Help", _ => "Page.Devices" });
    public bool IsScanning { get => _scanBusy; private set { Set(ref _scanBusy, value); Raise(nameof(ScanLabel)); (SaveSettingsCommand as AsyncCommand)?.NotifyCanExecuteChanged(); } }
    public string ScanLabel => L10n.Get(IsScanning ? "Device.Scanning" : "Device.Scan");
    public string Status => _status.Value;
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public int ConnectedCount => Devices.Count(device => device.IsConnected);
    public string ConnectedSummary => $"{ConnectedCount:00} / {Devices.Count:00}";
    public bool HasSelection => SelectedDevice is not null;
    public bool SelectedConnected => SelectedDevice?.IsConnected == true;
    public string SelectedName => SelectedDevice?.Name ?? L10n.Get("Device.None");
    public string SelectedKind => SelectedDevice?.Kind ?? L10n.Get("Device.ConnectHelp");
    public string SelectedId => SelectedDevice?.Id ?? L10n.Get("Device.NoneId");
    public string SelectedBattery => SelectedDevice?.Battery ?? "—";
    public string SelectedBatteryLabel => L10n.Format("Device.Battery", SelectedBattery);
    public string SelectedSignal => SelectedDevice?.Signal ?? "—";
    public bool HasElectrodeReadings => IsBluetooth && SelectedConnected && ShowContacts && SelectedDevice?.Device.Profile?.Contacts == true;
    public string SelectedContacts
    {
        get
        {
            _controller.Connections.TryGetValue(SelectedDevice?.Id ?? "", out var connection);
            return L10n.Format("Device.Contacts", L10n.Get($"Contact.{connection?.ContactA ?? ElectrodeContact.Unknown}"), L10n.Get($"Contact.{connection?.ContactB ?? ElectrodeContact.Unknown}"));
        }
    }
    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            if (_selectedDevice is not null) _selectedDevice.IsSelected = false;
            Set(ref _selectedDevice, value); if (value is not null) value.IsSelected = true;
            RefreshDevices();
        }
    }
    public string EditorName { get => WaveEditor.EditorName; set => WaveEditor.EditorName = value; }
    public WaveShape EditorShape { get => WaveEditor.EditorShape; set => WaveEditor.EditorShape = value; }
    public bool IsSampledEditor => WaveEditor.IsSampledEditor;
    public bool IsGeneratedEditor => WaveEditor.IsGeneratedEditor;
    public ShapeItem? SelectedShape { get => WaveEditor.SelectedShape; set => WaveEditor.SelectedShape = value; }
    public double EditorPeriod { get => WaveEditor.EditorPeriod; set => WaveEditor.EditorPeriod = value; }
    public double EditorFrequency { get => WaveEditor.EditorFrequency; set => WaveEditor.EditorFrequency = value; }
    public string EditorPoints { get => WaveEditor.EditorPoints; set => WaveEditor.EditorPoints = value; }
    public string EditorSamples { get => WaveEditor.EditorSamples; set => WaveEditor.EditorSamples = value; }
    public string TransferJson { get => _transferJson; set => Set(ref _transferJson, value); }
    public WaveformPreset? EditorPreview => WaveEditor.EditorPreview;

    public ICommand WorkspaceCommand { get; }
    public ICommand WavesCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand PlaybackCommand { get; }
    public ICommand EmergencyStopCommand { get; }
    public ICommand SaveWaveCommand { get; }
    public ICommand NewWaveCommand { get; }
    public ICommand EditWaveCommand { get; }
    public AsyncCommand DeleteWaveCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand UseWaveCommand { get; }
    public ICommand AddWaveModeCommand { get; }
    public ICommand AddTouchModeCommand { get; }
    public ICommand AddModeCommand { get; }
    public ICommand SaveModesCommand { get; }
    public ICommand RemoveModeCommand { get; }
    public MainViewModel(DeviceController controller, WorkspaceStore store, Func<IDeviceTransport>? bluetoothFactory = null, IAudioInputFactory? audioInputFactory = null)
    {
        _controller = controller; _store = store; _bluetoothFactory = bluetoothFactory ?? (() => new BluetoothDeviceTransport(new UnsupportedBluetoothAdapter()));
        AudioInputs = audioInputFactory ?? new UnavailableAudioInputFactory();
        _uiContext = SynchronizationContext.Current;
        WaveEditor.PropertyChanged += (_, args) => Raise(args.PropertyName);
        _selectedTransport = TransportOptions.First(option => option.UseBluetooth == !controller.IsSimulation);
        _selectedLanguage = Languages.First(language => language.Code == L10n.Current.Language);
        WorkspaceCommand = new AsyncCommand(() => NavigateAsync("workspace"), ShowError);
        WavesCommand = new AsyncCommand(() => NavigateAsync("waves"), ShowError);
        HistoryCommand = new AsyncCommand(() => NavigateAsync("history"), ShowError);
        SettingsCommand = new AsyncCommand(() => NavigateAsync("settings"), ShowError);
        HelpCommand = new AsyncCommand(() => NavigateAsync("help"), ShowError);
        ScanCommand = new AsyncCommand(ScanAsync, ShowError);
        PlaybackCommand = new AsyncCommand(TogglePlaybackAsync, ShowError, () => SelectedMode.IsRunning || SelectedMode.CanStart);
        EmergencyStopCommand = new AsyncCommand(() => StopAllModesAsync("Reason.AllStopped"), ShowError);
        SaveWaveCommand = new AsyncCommand(SaveWaveAsync, ShowError); NewWaveCommand = new ActionCommand(NewWave); EditWaveCommand = new ActionCommand(EditWave);
        DeleteWaveCommand = new AsyncCommand(DeleteWaveAsync, ShowError, () => LibrarySelectedPreset is { IsBuiltIn: false } selected && !Modes.Any(mode => mode.UsesWaveform(selected.Preset.Id)));
        ExportCommand = new ActionCommand(() => { TransferJson = WorkspaceStore.ExportPresets(Presets.Select(item => item.Preset)); Notify("Status.Generated"); });
        ImportCommand = new AsyncCommand(() => ImportAsync(TransferJson), ShowError);
        SaveSettingsCommand = new AsyncCommand(SaveSettingsAsync, ShowError, () => !IsScanning);
        UseWaveCommand = new AsyncCommand(async () =>
        {
            if (!CanUseLibraryWave) return;
            SelectedPreset = LibrarySelectedPreset; await NavigateAsync("workspace");
        }, ShowError, () => CanUseLibraryWave);
        AddWaveModeCommand = new AsyncCommand(() => AddModeAsync(false), ShowError); AddTouchModeCommand = new AsyncCommand(() => AddModeAsync(true), ShowError);
        AddModeCommand = new AsyncCommand(() => AddModeAsync((ModeKind)NewModeType.Value), ShowError);
        SaveModesCommand = new AsyncCommand(async () => { await SaveAsync(); Notify("Status.ModesSaved"); }, ShowError);
        RemoveModeCommand = new AsyncCommand(RemoveSelectedModeAsync, ShowError, () => Modes.Count > 1);
        InitializeDeviceFeatureCommands();
        AddMode(new(Guid.NewGuid(), L10n.Format("Mode.WaveName", 1)));
        _selectedMode = Modes[0];
        Presets.CollectionChanged += (_, _) => { foreach (var mode in Modes) mode.RefreshPlaylist(); };
        _controller.Changed += OnDeviceChanged; _controller.Faulted += OnTransportFaulted;
        L10n.Current.PropertyChanged += OnLanguageChanged;
    }
    public async Task InitializeAsync()
    {
        if (_settingsLoaded) return;
        var data = await _store.LoadAsync();
        SelectedLanguage = Languages.First(language => language.Code == JsonLocalizer.NormalizeLanguage(data.Settings.Language ?? JsonLocalizer.SystemLanguage));
        DefaultLimit = data.Settings.DefaultLimit; TimerLimitMinutes = data.Settings.TimerLimitMinutes;
        SelectedTransport = TransportOptions.First(option => option.UseBluetooth == data.Settings.UseBluetooth); await ApplyTransportAsync();
        foreach (var preset in WaveformPreset.BuiltIn) Presets.Add(new(preset));
        foreach (var preset in data.Presets.Where(preset => !WaveformPreset.BuiltIn.Any(builtIn => builtIn.Id == preset.Id))) Presets.Add(new(preset));
        Modes.Clear();
        foreach (var config in data.Modes ?? []) AddMode(config);
        if (Modes.Count == 0) AddMode(new(Guid.NewGuid(), L10n.Format("Mode.WaveName", 1), OutputLimit: DefaultLimit));
        SelectedMode = Modes[0];
        foreach (var record in data.Sessions.OrderByDescending(record => record.StartedAt)) { _records.Add(record); Sessions.Add(new(record)); }
        _statistics = data.Statistics ?? new(data.Sessions.Count, data.Sessions.Sum(record => record.DurationSeconds));
        _settingsLoaded = true; Raise(nameof(SessionCount)); Raise(nameof(TotalTime)); await ScanAsync();
        if (_store.LoadWarning is not null) ShowError(new LocalizedIOException("Error.CorruptWorkspace"));
    }
    private void AddMode(PlaybackModeConfig config)
    {
        var mode = new PlaybackModeViewModel(this, _controller, config); Modes.Add(mode);
        (RemoveModeCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        mode.PropertyChanged += (_, e) =>
        {
            if (mode == SelectedMode && e.PropertyName is { } name) Raise(name);
            Raise(nameof(IsAnyRunning)); Raise(nameof(RunningModesSummary));
            if (e.PropertyName == nameof(PlaybackModeViewModel.ShowContacts)) Raise(nameof(HasElectrodeReadings));
            if (e.PropertyName == nameof(PlaybackModeViewModel.CanEditRouting)) RefreshAssignableDevices();
            DeleteWaveCommand.NotifyCanExecuteChanged(); RefreshDeviceFeatures();
            (UseWaveCommand as AsyncCommand)?.NotifyCanExecuteChanged();
            (PlaybackCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        };
    }
    public Task AddModeAsync(bool touch) => AddModeAsync(touch ? ModeKind.Touch : ModeKind.Waveform);
    public async Task AddModeAsync(ModeKind kind)
    {
        if (Modes.Count >= 32) throw new LocalizedOperationException("Error.ModeCount");
        AddMode(new(Guid.NewGuid(), L10n.Format("Mode.InstanceName", L10n.Get($"ModeKind.{kind}"), Modes.Count + 1), kind,
            OutputLimit: DefaultLimit, DurationSeconds: Math.Min(600, (int)TimerLimitMinutes * 60)));
        SelectedMode = Modes[^1]; await SaveAsync();
    }
    private async Task RemoveSelectedModeAsync()
    {
        if (Modes.Count == 1) throw new LocalizedOperationException("Error.LastMode");
        var mode = SelectedMode; await mode.StopAsync("Reason.ModeRemoved", false);
        Modes.Remove(mode); SelectedMode = Modes[0]; RefreshModeGroups(); await SaveAsync();
    }
    private void RefreshModeBindings()
    {
        foreach (var property in typeof(PlaybackModeViewModel).GetProperties()) Raise(property.Name);
        Raise(nameof(SelectedMode)); Raise(nameof(HasElectrodeReadings));
        (RemoveModeCommand as AsyncCommand)?.NotifyCanExecuteChanged();
        DeleteWaveCommand.NotifyCanExecuteChanged(); RefreshDeviceFeatures();
        (UseWaveCommand as AsyncCommand)?.NotifyCanExecuteChanged();
    }
    public async Task ScanAsync()
    {
        if (IsScanning || _changingTransport) return;
        IsScanning = true;
        try
        {
            var found = await _controller.ScanAsync();
            foreach (var device in Devices.Where(device => !device.IsConnected && found.All(item => item.Id != device.Id)).ToArray()) Devices.Remove(device);
            foreach (var info in found)
            {
                var existing = Devices.FirstOrDefault(device => device.Id == info.Id);
                if (existing is not null) { existing.UpdateReadings(info); continue; }
                Devices.Add(new(info, Devices.Count + 1, SelectDeviceAsync, ToggleConnectionAsync, ShowError,
                    device => SelectedMode.AssignTarget(device.Id, true)));
            }
            SelectedDevice ??= Devices.FirstOrDefault(); RefreshDevices(); Notify(IsBluetooth ? "Status.BluetoothDiscovered" : "Status.Discovered", found.Count);
        }
        finally { IsScanning = false; }
    }
    public async Task SaveSettingsAsync()
    {
        if (IsScanning) throw new LocalizedOperationException("Error.BluetoothBusy");
        var changed = await ApplyTransportAsync(); await SaveAsync(); Notify("Status.SettingsSaved"); if (changed) await ScanAsync();
    }
    private async Task<bool> ApplyTransportAsync()
    {
        if (SelectedTransport is null || SelectedTransport.UseBluetooth == IsBluetooth) return false;
        _changingTransport = true;
        try
        {
            await StopAllModesAsync("Reason.TransportChanged");
            foreach (var id in _controller.Connections.Keys.ToArray()) await _controller.DisconnectAsync(id);
            await _controller.ChangeTransportAsync(SelectedTransport.UseBluetooth ? _bluetoothFactory() : new SimulationTransport());
            SelectedDevice = null; Devices.Clear(); RefreshTransport(); return true;
        }
        finally { _changingTransport = false; }
    }
    public Task SelectDeviceAsync(DeviceItem device) { SelectedDevice = device; return Task.CompletedTask; }
    internal PlaybackModeViewModel? AssignedMode(string id) => Modes.FirstOrDefault(mode => mode.TargetIds.Contains(id));
    internal void RefreshModeGroups()
    {
        foreach (var mode in Modes) mode.RefreshTargets();
        var available = Devices.Where(device => AssignedMode(device.Id) is null).ToArray();
        foreach (var device in UnassignedDevices.Where(device => !available.Contains(device)).ToArray()) UnassignedDevices.Remove(device);
        foreach (var device in available) if (!UnassignedDevices.Contains(device)) UnassignedDevices.Add(device);
        RefreshAssignableDevices(); Raise(nameof(HasUnassignedDevices));
    }
    private void RefreshAssignableDevices()
    {
        foreach (var device in Devices) device.CanAssign = SelectedMode.CanEditRouting && AssignedMode(device.Id) is null && SelectedMode.CanAssignDevice(device.Device);
    }
    public async Task ToggleConnectionAsync(DeviceItem device)
    {
        if (device.IsConnected)
        {
            List<Exception> errors = [];
            try { await StopModesForDeviceAsync(device.Id, "Reason.ConnectionChanged"); }
            catch (Exception error) { errors.Add(error); }
            try { await _controller.DisconnectAsync(device.Id); }
            catch (Exception error) { errors.Add(error); }
            finally { RefreshDevices(); }
            if (errors.Count > 0) throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
            Notify("Status.Disconnected", device.Name);
        }
        else
        {
            await _controller.ConnectAsync(device.Device); SelectedDevice = device;
            Notify("Status.Connected", device.Name);
        }
        RefreshDevices();
    }
    public Task TogglePlaybackAsync() => SelectedMode.TogglePlaybackAsync();
    public async Task TickAsync() { if (_disposed) return; foreach (var mode in Modes.ToArray()) await mode.TickAsync(); }
    public Task StopAsync(string reason = "Reason.Manual", bool notify = true) => SelectedMode.StopAsync(reason, notify);
    public async Task StopAllModesAsync(string reason = "Reason.AllStopped")
    {
        List<Exception> errors = [];
        foreach (var mode in Modes.ToArray()) try { await mode.StopAsync(reason, false); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
    }
    private async Task StopModesForDeviceAsync(string id, string reason)
    { foreach (var mode in Modes.Where(mode => mode.TargetIds.Contains(id)).ToArray()) await mode.StopAsync(reason, false); }
    public Task NavigateAsync(string page)
    {
        _page = page;
        foreach (var property in new[] { nameof(IsWorkspace), nameof(IsWaves), nameof(IsHistory), nameof(IsSettings), nameof(IsHelp), nameof(PageTitle) }) Raise(property);
        return Task.CompletedTask;
    }
    public void NewWave() => WaveEditor.Clear();
    public void EditWave()
    {
        if (LibrarySelectedPreset is not { } selected) return;
        WaveEditor.Load(selected);
        Notify(selected.IsBuiltIn ? "Status.CopyBuiltIn" : "Status.Editing");
    }

    public async Task SaveWaveAsync()
    {
        var preset = WaveEditor.BuildPreset();
        preset.Validate();
        if (Modes.Any(mode => mode.UsesWaveform(preset.Id))) throw new LocalizedOperationException("Error.WaveInUse");
        var references = Modes.Select(mode => (Mode: mode, A: mode.SelectedPreset?.Preset.Id == preset.Id, B: mode.SelectedPresetB?.Preset.Id == preset.Id)).ToArray();
        var next = Presets.Select(p => p.Preset).Where(p => p.Id != preset.Id).Append(preset).ToArray();
        await SaveAsync(next);
        var old = Presets.FirstOrDefault(p => p.Preset.Id == preset.Id);
        if (old is not null) Presets.Remove(old);
        var item = new PresetItem(preset); Presets.Add(item); LibrarySelectedPreset = item; WaveEditor.MarkSaved(preset.Id);
        foreach (var reference in references) { if (reference.A) reference.Mode.SelectedPreset = item; if (reference.B) reference.Mode.SelectedPresetB = item; }
        Notify("Status.WaveSaved", preset.Name);
    }

    public async Task DeleteWaveAsync()
    {
        if (LibrarySelectedPreset is not { } selected) return;
        if (selected.IsBuiltIn) throw new LocalizedOperationException("Error.DeleteBuiltIn");
        if (Modes.Any(mode => mode.UsesWaveform(selected.Preset.Id))) throw new LocalizedOperationException("Error.WaveInUse");
        foreach (var mode in Modes)
        {
            mode.RemovePlaylistWave(selected.Preset.Id);
            if (mode.SelectedPreset == selected) mode.SelectedPreset = Presets[0];
            if (mode.SelectedPresetB == selected) mode.SelectedPresetB = Presets[0];
        }
        await SaveAsync(Presets.Where(p => p != selected).Select(p => p.Preset));
        LibrarySelectedPreset = Presets[0]; Presets.Remove(selected); NewWave(); Notify("Status.WaveDeleted");
    }

    public async Task ImportAsync(string json)
    {
        var imported = WorkspaceStore.ImportPresets(json);
        var known = Presets.Select(p => p.Preset.Id).ToHashSet();
        var added = imported.Where(p => !known.Contains(p.Id)).ToArray();
        await SaveAsync(Presets.Select(p => p.Preset).Concat(added));
        foreach (var preset in added) Presets.Add(new(preset));
        Notify("Status.Imported", added.Length, imported.Count - added.Length);
    }


    public async Task SaveAsync(IEnumerable<WaveformPreset>? presets = null)
    {
        if (Presets.Count == 0) return;
        await _store.SaveAsync(new(WorkspaceData.CurrentSchema, (presets ?? Presets.Select(item => item.Preset)).Where(preset => !DefaultWaveformCatalog.Contains(preset.Id)).ToList(),
            _records.ToList(), new(DefaultLimit, SelectedLanguage?.Code, IsBluetooth, (int)TimerLimitMinutes), _statistics, Modes.Select(mode => mode.ToConfig()).ToList()));
        _statistics = new(Math.Max(_statistics.SessionCount, _store.Statistics.SessionCount), Math.Max(_statistics.TotalDurationSeconds, _store.Statistics.TotalDurationSeconds));
        _records.RemoveAll(record => _store.Journal.IsDiscarded(record.Id));
        foreach (var item in Sessions.Where(item => _store.Journal.IsDiscarded(item.Record.Id)).ToArray()) Sessions.Remove(item);
        Raise(nameof(SessionCount)); Raise(nameof(TotalTime));
    }
    internal void AddModeSession(SessionRecord record)
    {
        _records.Insert(0, record); Sessions.Insert(0, new(record)); _statistics = _statistics.Add(record);
        Raise(nameof(SessionCount)); Raise(nameof(TotalTime)); _ = GuardAsync(() => SaveAsync()); Notify("Status.SessionSaved", new StoredReason(record.Reason));
    }
    private void OnDeviceChanged(object? sender, EventArgs e) => RunOnUi(RefreshDevices);
    private void OnTransportFaulted(object? sender, DeviceTransportFault fault) => RunOnUi(() => _ = GuardAsync(async () =>
    { await StopModesForDeviceAsync(fault.DeviceId, "Reason.ConnectionError"); RefreshDevices(); ShowError(fault.Error); }));
    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext) _uiContext.Post(_ => { if (!_disposed) action(); }, null); else action();
    }
    private void RefreshDevices()
    {
        foreach (var device in Devices)
        {
            device.IsConnected = _controller.Connections.TryGetValue(device.Id, out var connection);
            device.OutputA = connection is null ? 0 : WaveformRendering.Strength(connection.Output, 0);
            device.OutputB = connection is null ? 0 : WaveformRendering.Strength(connection.Output, 1);
            device.ActiveModeName = _controller.DeviceOwner(device.Id) is { } owner ? Modes.FirstOrDefault(mode => mode.Id == owner)?.Name : null;
            if (connection is not null) device.UpdateReadings(connection.Device);
        }
        RefreshModeGroups();
        foreach (var name in new[] { nameof(ConnectedCount), nameof(ConnectedSummary), nameof(SelectedConnected), nameof(SelectedName), nameof(SelectedKind), nameof(SelectedId), nameof(HasSelection),
            nameof(SelectedBattery), nameof(SelectedBatteryLabel), nameof(SelectedSignal), nameof(SelectedContacts), nameof(HasElectrodeReadings) }) Raise(name);
        RefreshDeviceFeatures();
        RefreshTelemetry();
    }
    private void RefreshTransport()
    {
        foreach (var name in new[] { nameof(IsBluetooth), nameof(TransportName), nameof(ModeLabel), nameof(DriverLabel), nameof(DeviceHelp), nameof(TransportHelp), nameof(ReadingsLabel) }) Raise(name);
        RefreshDevices();
    }
    public void ShowError(Exception exception)
    {
        Trace.TraceError("{0}", exception);
        _status = exception switch
        {
            ILocalizedError error => error.LocalizedMessage,
            JsonException => new("Error.InvalidJson", []),
            FormatException or OverflowException => new("Error.InvalidNumber", []),
            _ => new(exception.Message, [], true)
        };
        IsError = true; Raise(nameof(Status)); Log(_status);
    }
    internal void ShowValidationError(string key)
    {
        _status = new(key, []); IsError = true; Raise(nameof(Status)); Log(_status);
    }
    public void Notify(string key, params object?[] arguments)
    {
        _status = new(key, arguments); IsError = false; Raise(nameof(Status)); Log(_status);
    }
    private void Log(LocalizedMessage text)
    {
        Activities.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), text));
        while (Activities.Count > 20) Activities.RemoveAt(Activities.Count - 1);
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language)) return;
        _selectedLanguage = Languages.First(language => language.Code == L10n.Current.Language); Raise(nameof(SelectedLanguage));
        foreach (var item in Devices) item.RefreshLocalization(); foreach (var item in Presets) item.RefreshLocalization();
        foreach (var item in Shapes) item.RefreshLocalization(); foreach (var item in Sessions) item.RefreshLocalization();
        foreach (var item in Activities) item.RefreshLocalization(); foreach (var item in TransportOptions) item.RefreshLocalization();
        foreach (var mode in Modes) mode.RefreshLocalization();
        foreach (var kind in ModeTypes) kind.RefreshLocalization();
        RefreshTransport(); RefreshModeBindings();
        foreach (var name in new[] { nameof(PageTitle), nameof(ScanLabel), nameof(Status), nameof(TotalTime), nameof(RunningModesSummary) }) Raise(name);
    }
    private async Task GuardAsync(Func<Task> action) { try { await action(); } catch (Exception error) { ShowError(error); } }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        try { await StopAllModesAsync("Reason.Close"); await SaveAsync(); }
        finally
        {
            _controller.Changed -= OnDeviceChanged; _controller.Faulted -= OnTransportFaulted;
            L10n.Current.PropertyChanged -= OnLanguageChanged; await _controller.DisposeAsync();
        }
    }
}
