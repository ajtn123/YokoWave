using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly SessionEngine _engine;
    private readonly WorkspaceStore _store;
    private readonly Stopwatch _clock = new();
    private bool _tickBusy;
    private bool _scanBusy;
    private bool _disposed;
    private string _page = "workspace";
    private LocalizedMessage _status = new("Status.NoDevice", []);
    private bool _settingsLoaded;
    private LanguageOption? _selectedLanguage;
    private bool _isError;
    private DeviceItem? _selectedDevice;
    private PresetItem? _selectedPreset;
    private double _channelA;
    private double _channelB;
    private double _safetyLimit = 60;
    private double _defaultLimit = 60;
    private double _sessionMinutes = 10;
    private bool _offsetChannelB = true;
    private bool _linkMode;
    private string _editorName = "";
    private WaveShape _editorShape = WaveShape.Sine;
    private double _editorPeriod = 4;
    private double _editorFrequency = 20;
    private string _editorPoints = "0, 0.2, 0.8, 1, 0.8, 0.2, 0";
    private string _editorSamples = "[]";
    private WaveformFamily _editorFamily;
    private Guid? _editingId;
    private string _transferJson = "";
    private readonly List<SessionRecord> _records = [];

    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public ObservableCollection<PresetItem> Presets { get; } = [];
    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<ActivityItem> Activities { get; } = [];
    public IReadOnlyList<ShapeItem> Shapes { get; } = Enum.GetValues<WaveShape>().Select(s => new ShapeItem(s)).ToArray();
    public IReadOnlyList<LanguageOption> Languages { get; } =
        [new("en", L10n.Current.English["Language.en"]), new("zh-CN", L10n.Current.English["Language.zh-CN"])];
    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !Set(ref _selectedLanguage, value)) return;
            L10n.Current.SetLanguage(value.Code);
            if (_settingsLoaded) _ = GuardAsync(() => SaveAsync());
        }
    }
    public bool IsWorkspace => _page == "workspace";
    public bool IsWaves => _page == "waves";
    public bool IsHistory => _page == "history";
    public bool IsSettings => _page == "settings";
    public string PageTitle => _page switch { "waves" => L10n.Get("Page.Waves"), "history" => L10n.Get("Page.History"), "settings" => L10n.Get("Page.Settings"), _ => L10n.Get("Page.Devices") };
    public bool IsScanning { get => _scanBusy; private set { Set(ref _scanBusy, value); Raise(nameof(ScanLabel)); } }
    public string ScanLabel => L10n.Get(IsScanning ? "Device.Scanning" : "Device.Scan");
    public string Status => _status.Value;
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool IsRunning => _engine.IsRunning;
    public bool CanEditRouting => !IsRunning;
    public int ConnectedCount => Devices.Count(d => d.IsConnected);
    public string ConnectedSummary => $"{ConnectedCount:00} / {Devices.Count:00}";
    public string SessionState => IsRunning ? L10n.Get("Session.Running") : L10n.Get("Session.Idle");
    public string PlaybackLabel => IsRunning ? L10n.Get("Session.Stop") : L10n.Get("Session.Start");
    public string Elapsed => FormatDuration(_engine.Elapsed);
    public string Remaining => FormatDuration(TimeSpan.FromSeconds(Math.Max(0, SessionMinutes * 60 - _engine.Elapsed.TotalSeconds)));
    public double Playhead => IsRunning ? _engine.Elapsed.TotalSeconds / 12 % 1 : 0;
    public string TransportName => _controller.TransportName;
    public string StoragePath => _store.FilePath;
    public int SessionCount => Sessions.Count;
    public string TotalTime => L10n.Format("History.TotalFormat", _records.Sum(r => r.DurationSeconds) / 60);
    public string SelectedName => SelectedDevice?.Name ?? L10n.Get("Device.None");
    public string SelectedKind => SelectedDevice?.Kind ?? L10n.Get("Device.ConnectHelp");
    public string SelectedId => SelectedDevice?.Id ?? L10n.Get("Device.NoneId");
    public bool HasSelection => SelectedDevice is not null;
    public bool SelectedConnected => SelectedDevice?.IsConnected == true;
    public bool HasChannelB => SelectedDevice?.Device.Kind == DeviceKind.DualChannel;
    public string SelectedBattery => SelectedDevice?.Battery ?? "—";
    public string SelectedBatteryLabel => L10n.Format("Device.Battery", SelectedBattery);
    public string SelectedSignal => SelectedDevice?.Signal ?? "—";
    public string RoutingSummary => LinkMode ? L10n.Format("Device.LinkedCount", Devices.Count(d => d.IsConnected && d.IsIncluded)) : L10n.Get("Device.CurrentOnly");
    public string PresetSummary => SelectedPreset?.Detail ?? L10n.Get("Wave.Select");

    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            if (_selectedDevice is not null) _selectedDevice.IsSelected = false;
            Set(ref _selectedDevice, value);
            if (value is not null) value.IsSelected = true;
            foreach (var property in new[] { nameof(SelectedName), nameof(SelectedKind), nameof(SelectedId), nameof(HasSelection),
                nameof(SelectedConnected), nameof(HasChannelB), nameof(SelectedBattery), nameof(SelectedBatteryLabel), nameof(SelectedSignal) }) Raise(property);
        }
    }
    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value)) return;
            Raise(nameof(PresetSummary));
            DeleteWaveCommand.NotifyCanExecuteChanged();
            if (IsRunning) _ = GuardAsync(() => StopAsync("Reason.WaveChanged"));
        }
    }
    public double ChannelA { get => _channelA; set { Set(ref _channelA, Math.Clamp(value, 0, SafetyLimit)); _engine.ChannelA = _channelA; } }
    public double ChannelB { get => _channelB; set { Set(ref _channelB, Math.Clamp(value, 0, SafetyLimit)); _engine.ChannelB = _channelB; } }
    public double SafetyLimit
    {
        get => _safetyLimit;
        set
        {
            if (!Set(ref _safetyLimit, Math.Clamp(value, 0, 100))) return;
            ChannelA = Math.Min(ChannelA, value);
            ChannelB = Math.Min(ChannelB, value);
            if (SelectedDevice is { IsConnected: true } device)
                _ = GuardAsync(() => _controller.SetLimitAsync(device.Id, _safetyLimit));
        }
    }
    public double DefaultLimit { get => _defaultLimit; set => Set(ref _defaultLimit, Math.Clamp(value, 0, 100)); }
    public double SessionMinutes { get => _sessionMinutes; set => Set(ref _sessionMinutes, Math.Clamp(value, 1, 120)); }
    public bool OffsetChannelB { get => _offsetChannelB; set { Set(ref _offsetChannelB, value); _engine.OffsetChannelB = value; } }
    public bool LinkMode { get => _linkMode; set { Set(ref _linkMode, value); Raise(nameof(RoutingSummary)); } }
    public string EditorName { get => _editorName; set { Set(ref _editorName, value); Raise(nameof(EditorPreview)); } }
    public WaveShape EditorShape { get => _editorShape; set { Set(ref _editorShape, value); Raise(nameof(SelectedShape)); Raise(nameof(EditorPreview)); Raise(nameof(IsSampledEditor)); Raise(nameof(IsGeneratedEditor)); } }
    public bool IsSampledEditor => EditorShape == WaveShape.Sampled;
    public bool IsGeneratedEditor => !IsSampledEditor;
    public ShapeItem? SelectedShape { get => Shapes.FirstOrDefault(s => s.Shape == EditorShape); set { if (value is not null) EditorShape = value.Shape; } }
    public double EditorPeriod { get => _editorPeriod; set { Set(ref _editorPeriod, value); Raise(nameof(EditorPreview)); } }
    public double EditorFrequency { get => _editorFrequency; set { Set(ref _editorFrequency, value); Raise(nameof(EditorPreview)); } }
    public string EditorPoints { get => _editorPoints; set { Set(ref _editorPoints, value); Raise(nameof(EditorPreview)); } }
    public string EditorSamples { get => _editorSamples; set { Set(ref _editorSamples, value); Raise(nameof(EditorPreview)); } }
    public string TransferJson { get => _transferJson; set => Set(ref _transferJson, value); }
    public WaveformPreset? EditorPreview
    {
        get
        {
            try { var preset = BuildEditorPreset(); preset.Validate(); return preset; }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException or JsonException) { return null; }
        }
    }

    public ICommand WorkspaceCommand { get; }
    public ICommand WavesCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand SettingsCommand { get; }
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

    public MainViewModel(DeviceController controller, WorkspaceStore store)
    {
        _controller = controller;
        _store = store;
        _selectedLanguage = Languages.First(l => l.Code == L10n.Current.Language);
        L10n.Current.PropertyChanged += OnLanguageChanged;
        _engine = new(controller);
        _controller.Changed += OnDeviceChanged;
        _engine.Completed += OnSessionCompleted;
        WorkspaceCommand = new AsyncCommand(() => NavigateAsync("workspace"), ShowError);
        WavesCommand = new AsyncCommand(() => NavigateAsync("waves"), ShowError);
        HistoryCommand = new AsyncCommand(() => NavigateAsync("history"), ShowError);
        SettingsCommand = new AsyncCommand(() => NavigateAsync("settings"), ShowError);
        ScanCommand = new AsyncCommand(ScanAsync, ShowError);
        PlaybackCommand = new AsyncCommand(TogglePlaybackAsync, ShowError);
        EmergencyStopCommand = new AsyncCommand(() => StopAsync("Reason.AllStopped"), ShowError);
        SaveWaveCommand = new AsyncCommand(SaveWaveAsync, ShowError);
        NewWaveCommand = new ActionCommand(NewWave);
        EditWaveCommand = new ActionCommand(EditWave);
        DeleteWaveCommand = new AsyncCommand(DeleteWaveAsync, ShowError, () => SelectedPreset is { IsBuiltIn: false });
        ExportCommand = new ActionCommand(() => { TransferJson = WorkspaceStore.ExportPresets(Presets.Select(p => p.Preset)); Notify("Status.Generated"); });
        ImportCommand = new AsyncCommand(() => ImportAsync(TransferJson), ShowError);
        SaveSettingsCommand = new AsyncCommand(async () => { await SaveAsync(); Notify("Status.SettingsSaved"); }, ShowError);
        UseWaveCommand = new AsyncCommand(() => NavigateAsync("workspace"), ShowError);
    }

    public async Task InitializeAsync()
    {
        var data = await _store.LoadAsync();
        SelectedLanguage = Languages.First(l => l.Code == JsonLocalizer.NormalizeLanguage(data.Settings.Language ?? JsonLocalizer.SystemLanguage));
        DefaultLimit = data.Settings.DefaultLimit;
        SessionMinutes = data.Settings.SessionMinutes;
        OffsetChannelB = data.Settings.OffsetChannelB;
        foreach (var preset in WaveformPreset.BuiltIn) Presets.Add(new(preset));
        foreach (var preset in data.Presets.Where(p => !WaveformPreset.BuiltIn.Any(b => b.Id == p.Id))) Presets.Add(new(preset));
        foreach (var record in data.Sessions.OrderByDescending(r => r.StartedAt)) { _records.Add(record); Sessions.Add(new(record)); }
        SelectedPreset = Presets.First();
        _settingsLoaded = true;
        Raise(nameof(SessionCount)); Raise(nameof(TotalTime));
        await ScanAsync();
        if (_store.LoadWarning is not null) ShowError(new LocalizedIOException("Error.CorruptWorkspace"));
    }

    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var found = await _controller.ScanAsync();
            foreach (var item in found)
            {
                if (Devices.Any(d => d.Id == item.Id)) continue;
                var device = new DeviceItem(item, Devices.Count + 1, SelectDeviceAsync, ToggleConnectionAsync, ShowError);
                device.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DeviceItem.IsIncluded)) Raise(nameof(RoutingSummary)); };
                Devices.Add(device);
            }
            if (SelectedDevice is null && Devices.Count > 0) SelectedDevice = Devices[0];
            Notify("Status.Discovered", found.Count);
            Raise(nameof(ConnectedSummary));
        }
        finally { IsScanning = false; }
    }

    public async Task SelectDeviceAsync(DeviceItem device)
    {
        if (SelectedDevice == device) return;
        await StopAsync("Reason.DeviceChanged", false);
        SelectedDevice = device;
        SafetyLimit = _controller.Connections.TryGetValue(device.Id, out var connection) ? connection.Limit : DefaultLimit;
        ChannelA = ChannelB = 0;
    }

    public async Task ToggleConnectionAsync(DeviceItem device)
    {
        await StopAsync("Reason.ConnectionChanged", false);
        if (device.IsConnected)
        {
            await _controller.DisconnectAsync(device.Id);
            Notify("Status.Disconnected", device.Name);
        }
        else
        {
            await _controller.ConnectAsync(device.Device, DefaultLimit);
            await SelectDeviceAsync(device);
            Notify("Status.Connected", device.Name);
        }
        RefreshDevices();
    }

    public async Task TogglePlaybackAsync()
    {
        if (IsRunning) { await StopAsync(); return; }
        if (SelectedPreset is null) throw new LocalizedOperationException("Error.SelectWave");
        var targets = LinkMode ? Devices.Where(d => d.IsConnected && d.IsIncluded).Select(d => d.Id).ToArray()
            : SelectedDevice is { IsConnected: true } device ? [device.Id] : Array.Empty<string>();
        if (ChannelA <= 0 && (!HasChannelB || ChannelB <= 0))
            throw new LocalizedOperationException("Error.SetLevel");
        await _engine.StartAsync(targets, SelectedPreset.Preset, TimeSpan.FromMinutes(SessionMinutes));
        _clock.Restart();
        RefreshPlayback();
        Notify("Status.Started", SelectedPreset, targets.Length);
    }

    public async Task TickAsync()
    {
        if (_tickBusy || _disposed || !IsRunning) return;
        _tickBusy = true;
        try { await _engine.AdvanceAsync(_clock.Elapsed); RefreshPlayback(); }
        catch (Exception e) { ShowError(e); RefreshPlayback(); }
        finally { _tickBusy = false; }
    }

    public async Task StopAsync(string reason = "Reason.Manual", bool notify = true)
    {
        await _engine.StopAsync(reason);
        _clock.Stop();
        RefreshPlayback();
        if (notify) Notify("Status.Stopped");
    }

    public async Task NavigateAsync(string page)
    {
        await StopAsync("Reason.PageChanged", false);
        _page = page;
        foreach (var property in new[] { nameof(IsWorkspace), nameof(IsWaves), nameof(IsHistory), nameof(IsSettings), nameof(PageTitle) }) Raise(property);
    }

    public void NewWave()
    {
        _editingId = null;
        EditorName = ""; EditorSamples = "[]"; _editorFamily = WaveformFamily.Generic;
        EditorShape = WaveShape.Sine; EditorPeriod = 4; EditorFrequency = 20;
    }

    public void EditWave()
    {
        if (SelectedPreset is null) return;
        var preset = SelectedPreset.Preset;
        _editingId = SelectedPreset.IsBuiltIn ? null : preset.Id;
        EditorName = SelectedPreset.IsBuiltIn ? L10n.Format("Wave.CopyName", preset.Name) : preset.Name;
        EditorShape = preset.Shape; _editorFamily = preset.Family;
        EditorSamples = JsonSerializer.Serialize(preset.Samples ?? [], new JsonSerializerOptions { WriteIndented = true });
        EditorPeriod = preset.PeriodSeconds; EditorFrequency = preset.Frequency;
        EditorPoints = string.Join(", ", preset.Points?.Select(p => p.ToString("0.###", CultureInfo.InvariantCulture)) ?? ["0", "1", "0"]);
        Notify(SelectedPreset.IsBuiltIn ? "Status.CopyBuiltIn" : "Status.Editing");
    }

    public async Task SaveWaveAsync()
    {
        var preset = BuildEditorPreset();
        preset.Validate();
        var next = Presets.Select(p => p.Preset).Where(p => p.Id != preset.Id).Append(preset).ToArray();
        await SaveAsync(next);
        var old = Presets.FirstOrDefault(p => p.Preset.Id == preset.Id);
        if (old is not null) Presets.Remove(old);
        var item = new PresetItem(preset); Presets.Add(item); SelectedPreset = item; _editingId = preset.Id;
        Notify("Status.WaveSaved", preset.Name);
    }

    private WaveformPreset BuildEditorPreset()
    {
        var points = EditorShape == WaveShape.Custom ? EditorPoints.Split([',', '，', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray() : null;
        var samples = IsSampledEditor ? JsonSerializer.Deserialize<WaveformSample[]>(EditorSamples,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
        return new(_editingId ?? Guid.NewGuid(), EditorName.Trim(), EditorShape,
            EditorPeriod, EditorFrequency, points, samples, IsSampledEditor ? _editorFamily : WaveformFamily.Generic);
    }

    public async Task DeleteWaveAsync()
    {
        if (SelectedPreset is null) return;
        if (SelectedPreset.IsBuiltIn) throw new LocalizedOperationException("Error.DeleteBuiltIn");
        var selected = SelectedPreset;
        await SaveAsync(Presets.Where(p => p != selected).Select(p => p.Preset));
        SelectedPreset = Presets[0]; Presets.Remove(selected); NewWave(); Notify("Status.WaveDeleted");
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

    public Task SaveAsync(IEnumerable<WaveformPreset>? presets = null) => _store.SaveAsync(new(1,
        (presets ?? Presets.Select(p => p.Preset)).Where(p => !WaveformPreset.BuiltIn.Any(b => b.Id == p.Id)).ToList(),
        _records.Take(500).ToList(), new(DefaultLimit, SessionMinutes, OffsetChannelB, SelectedLanguage?.Code)));

    private void OnDeviceChanged(object? sender, EventArgs e) => RefreshDevices();
    private void OnSessionCompleted(object? sender, SessionRecord record)
    {
        _records.Insert(0, record); Sessions.Insert(0, new(record));
        while (Sessions.Count > 500) Sessions.RemoveAt(Sessions.Count - 1);
        while (_records.Count > 500) _records.RemoveAt(_records.Count - 1);
        Raise(nameof(SessionCount)); Raise(nameof(TotalTime));
        _ = GuardAsync(() => SaveAsync());
        Notify("Status.SessionSaved", new StoredReason(record.Reason));
    }

    private void RefreshDevices()
    {
        foreach (var device in Devices)
        {
            device.CanEditRouting = !IsRunning;
            device.IsConnected = _controller.Connections.TryGetValue(device.Id, out var connection);
            device.OutputA = connection?.Output.ChannelA ?? 0;
            device.OutputB = connection?.Output.ChannelB ?? 0;
        }
        Raise(nameof(ConnectedCount)); Raise(nameof(ConnectedSummary)); Raise(nameof(SelectedConnected)); Raise(nameof(RoutingSummary));
    }

    private void RefreshPlayback()
    {
        foreach (var property in new[] { nameof(IsRunning), nameof(CanEditRouting), nameof(SessionState), nameof(PlaybackLabel), nameof(Elapsed), nameof(Remaining), nameof(Playhead) }) Raise(property);
        RefreshDevices();
    }

    public void ShowError(Exception exception)
    {
        _status = exception switch
        {
            ILocalizedError error => error.LocalizedMessage,
            JsonException => new("Error.InvalidJson", []),
            FormatException or OverflowException => new("Error.InvalidNumber", []),
            _ => new(exception.Message, [], true)
        };
        IsError = true; Raise(nameof(Status)); Log(_status);
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
        _selectedLanguage = Languages.First(l => l.Code == L10n.Current.Language);
        Raise(nameof(SelectedLanguage));
        foreach (var item in Devices) item.RefreshLocalization();
        foreach (var item in Presets) item.RefreshLocalization();
        foreach (var item in Shapes) item.RefreshLocalization();
        foreach (var item in Sessions) item.RefreshLocalization();
        foreach (var item in Activities) item.RefreshLocalization();
        foreach (var name in new[] { nameof(PageTitle), nameof(ScanLabel), nameof(Status), nameof(SessionState),
            nameof(PlaybackLabel), nameof(TotalTime), nameof(SelectedName), nameof(SelectedKind), nameof(SelectedId),
            nameof(SelectedBatteryLabel), nameof(RoutingSummary), nameof(PresetSummary), nameof(TransportName) }) Raise(name);
        Raise(nameof(SelectedPreset));
    }
    private async Task GuardAsync(Func<Task> action) { try { await action(); } catch (Exception e) { ShowError(e); } }
    private static string FormatDuration(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await StopAsync("Reason.Close", false);
            await SaveAsync();
        }
        finally
        {
            _controller.Changed -= OnDeviceChanged;
            _engine.Completed -= OnSessionCompleted;
            L10n.Current.PropertyChanged -= OnLanguageChanged;
            await _controller.DisposeAsync();
        }
    }
}
