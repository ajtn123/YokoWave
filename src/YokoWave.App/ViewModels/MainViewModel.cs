using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using YokoWave.Core;

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
    private string _status = "工作区已就绪，连接模拟设备开始探索。";
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
    private string _editorName = "我的新波形";
    private WaveShape _editorShape = WaveShape.Sine;
    private double _editorPeriod = 4;
    private double _editorFrequency = 20;
    private string _editorPoints = "0, 0.2, 0.8, 1, 0.8, 0.2, 0";
    private string _editorDescription = "为自己的节奏命名";
    private Guid? _editingId;
    private string _transferJson = "";
    private readonly List<SessionRecord> _records = [];

    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public ObservableCollection<PresetItem> Presets { get; } = [];
    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<ActivityItem> Activities { get; } = [];
    public IReadOnlyList<WaveShape> Shapes { get; } = Enum.GetValues<WaveShape>();
    public bool IsWorkspace => _page == "workspace";
    public bool IsWaves => _page == "waves";
    public bool IsHistory => _page == "history";
    public bool IsSettings => _page == "settings";
    public string PageTitle => _page switch { "waves" => "波形资料库", "history" => "会话记录", "settings" => "偏好设置", _ => "设备工作台" };
    public string PageSubtitle => _page switch
    {
        "waves" => "把每一种节奏，变成你的专属波形。",
        "history" => "每一次连接与律动，都有迹可循。",
        "settings" => "让工作区以你熟悉的方式运转。",
        _ => "连接设备，找到属于你的节奏。"
    };
    public bool IsScanning { get => _scanBusy; private set { Set(ref _scanBusy, value); Raise(nameof(ScanLabel)); } }
    public string ScanLabel => IsScanning ? "正在发现…" : "＋  发现设备";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }
    public bool IsRunning => _engine.IsRunning;
    public bool CanEditRouting => !IsRunning;
    public int ConnectedCount => Devices.Count(d => d.IsConnected);
    public string ConnectedSummary => $"{ConnectedCount:00} / {Devices.Count:00}";
    public string SessionState => IsRunning ? "正在输出" : "待机就绪";
    public string PlaybackLabel => IsRunning ? "■  停止会话" : "▶  开始会话";
    public string Elapsed => FormatDuration(_engine.Elapsed);
    public string Remaining => FormatDuration(TimeSpan.FromSeconds(Math.Max(0, SessionMinutes * 60 - _engine.Elapsed.TotalSeconds)));
    public double Playhead => IsRunning ? _engine.Elapsed.TotalSeconds / 12 % 1 : 0;
    public string TransportName => _controller.TransportName;
    public string StoragePath => _store.FilePath;
    public int SessionCount => Sessions.Count;
    public string TotalTime => $"{_records.Sum(r => r.DurationSeconds) / 60:0.#} min";
    public string SelectedName => SelectedDevice?.Name ?? "选择你的设备";
    public string SelectedKind => SelectedDevice?.Kind ?? "连接后即可调节双通道与输出上限";
    public string SelectedId => SelectedDevice?.Id ?? "尚未选择";
    public bool HasSelection => SelectedDevice is not null;
    public bool SelectedConnected => SelectedDevice?.IsConnected == true;
    public bool HasChannelB => SelectedDevice?.Device.Kind == DeviceKind.DualChannel;
    public string SelectedBattery => SelectedDevice?.Battery ?? "—";
    public string SelectedSignal => SelectedDevice?.Signal ?? "—";
    public string RoutingSummary => LinkMode ? $"联动 {Devices.Count(d => d.IsConnected && d.IsIncluded)} 台设备" : "仅控制当前设备";
    public string PresetSummary => SelectedPreset?.Detail ?? "选择波形";

    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            if (_selectedDevice is not null) _selectedDevice.IsSelected = false;
            Set(ref _selectedDevice, value);
            if (value is not null) value.IsSelected = true;
            foreach (var property in new[] { nameof(SelectedName), nameof(SelectedKind), nameof(SelectedId), nameof(HasSelection),
                nameof(SelectedConnected), nameof(HasChannelB), nameof(SelectedBattery), nameof(SelectedSignal) }) Raise(property);
        }
    }
    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value)) return;
            Raise(nameof(PresetSummary));
            if (IsRunning) _ = GuardAsync(() => StopAsync("切换波形"));
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
    public string EditorName { get => _editorName; set => Set(ref _editorName, value); }
    public WaveShape EditorShape { get => _editorShape; set { Set(ref _editorShape, value); Raise(nameof(EditorPreview)); } }
    public double EditorPeriod { get => _editorPeriod; set { Set(ref _editorPeriod, value); Raise(nameof(EditorPreview)); } }
    public double EditorFrequency { get => _editorFrequency; set { Set(ref _editorFrequency, value); Raise(nameof(EditorPreview)); } }
    public string EditorPoints { get => _editorPoints; set { Set(ref _editorPoints, value); Raise(nameof(EditorPreview)); } }
    public string EditorDescription { get => _editorDescription; set => Set(ref _editorDescription, value); }
    public string TransferJson { get => _transferJson; set => Set(ref _transferJson, value); }
    public WaveformPreset? EditorPreview
    {
        get
        {
            try { var preset = BuildEditorPreset(); preset.Validate(); return preset; }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) { return null; }
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
    public ICommand DeleteWaveCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand UseWaveCommand { get; }

    public MainViewModel(DeviceController controller, WorkspaceStore store)
    {
        _controller = controller;
        _store = store;
        _engine = new(controller);
        _controller.Changed += OnDeviceChanged;
        _engine.Completed += OnSessionCompleted;
        WorkspaceCommand = new AsyncCommand(() => NavigateAsync("workspace"), ShowError);
        WavesCommand = new AsyncCommand(() => NavigateAsync("waves"), ShowError);
        HistoryCommand = new AsyncCommand(() => NavigateAsync("history"), ShowError);
        SettingsCommand = new AsyncCommand(() => NavigateAsync("settings"), ShowError);
        ScanCommand = new AsyncCommand(ScanAsync, ShowError);
        PlaybackCommand = new AsyncCommand(TogglePlaybackAsync, ShowError);
        EmergencyStopCommand = new AsyncCommand(() => StopAsync("全部停止"), ShowError);
        SaveWaveCommand = new AsyncCommand(SaveWaveAsync, ShowError);
        NewWaveCommand = new ActionCommand(NewWave);
        EditWaveCommand = new ActionCommand(EditWave);
        DeleteWaveCommand = new AsyncCommand(DeleteWaveAsync, ShowError);
        ExportCommand = new ActionCommand(() => { TransferJson = WorkspaceStore.ExportPresets(Presets.Select(p => p.Preset)); Notify("已生成全部波形的 JSON，可复制或导出文件。"); });
        ImportCommand = new AsyncCommand(() => ImportAsync(TransferJson), ShowError);
        SaveSettingsCommand = new AsyncCommand(async () => { await SaveAsync(); Notify("偏好设置已保存。"); }, ShowError);
        UseWaveCommand = new AsyncCommand(() => NavigateAsync("workspace"), ShowError);
    }

    public async Task InitializeAsync()
    {
        var data = await _store.LoadAsync();
        DefaultLimit = data.Settings.DefaultLimit;
        SessionMinutes = data.Settings.SessionMinutes;
        OffsetChannelB = data.Settings.OffsetChannelB;
        foreach (var preset in WaveformPreset.BuiltIn) Presets.Add(new(preset));
        foreach (var preset in data.Presets.Where(p => !WaveformPreset.BuiltIn.Any(b => b.Id == p.Id))) Presets.Add(new(preset));
        foreach (var record in data.Sessions.OrderByDescending(r => r.StartedAt)) { _records.Add(record); Sessions.Add(new(record)); }
        SelectedPreset = Presets.First();
        Raise(nameof(SessionCount)); Raise(nameof(TotalTime));
        await ScanAsync();
        if (_store.LoadWarning is { } warning) ShowError(new IOException(warning));
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
            Notify($"发现 {found.Count} 台本地模拟设备。未扫描真实蓝牙设备。");
            Raise(nameof(ConnectedSummary));
        }
        finally { IsScanning = false; }
    }

    public async Task SelectDeviceAsync(DeviceItem device)
    {
        if (SelectedDevice == device) return;
        await StopAsync("切换设备", false);
        SelectedDevice = device;
        SafetyLimit = _controller.Connections.TryGetValue(device.Id, out var connection) ? connection.Limit : DefaultLimit;
        ChannelA = ChannelB = 0;
    }

    public async Task ToggleConnectionAsync(DeviceItem device)
    {
        await StopAsync("连接变更", false);
        if (device.IsConnected)
        {
            await _controller.DisconnectAsync(device.Id);
            Notify($"{device.Name} 已断开连接。");
        }
        else
        {
            await _controller.ConnectAsync(device.Device, DefaultLimit);
            await SelectDeviceAsync(device);
            Notify($"{device.Name} 已连接，输出归零。");
        }
        RefreshDevices();
    }

    public async Task TogglePlaybackAsync()
    {
        if (IsRunning) { await StopAsync(); return; }
        if (SelectedPreset is null) throw new InvalidOperationException("请先选择波形。");
        var targets = LinkMode ? Devices.Where(d => d.IsConnected && d.IsIncluded).Select(d => d.Id).ToArray()
            : SelectedDevice is { IsConnected: true } device ? [device.Id] : Array.Empty<string>();
        if (ChannelA <= 0 && (!HasChannelB || ChannelB <= 0))
            throw new InvalidOperationException("先调节通道强度，再开始会话。");
        await _engine.StartAsync(targets, SelectedPreset.Preset, TimeSpan.FromMinutes(SessionMinutes));
        _clock.Restart();
        RefreshPlayback();
        Notify($"会话开始 · {SelectedPreset.Name} · {targets.Length} 台模拟设备。");
    }

    public async Task TickAsync()
    {
        if (_tickBusy || _disposed || !IsRunning) return;
        _tickBusy = true;
        try { await _engine.AdvanceAsync(_clock.Elapsed); RefreshPlayback(); }
        catch (Exception e) { ShowError(e); RefreshPlayback(); }
        finally { _tickBusy = false; }
    }

    public async Task StopAsync(string reason = "手动停止", bool notify = true)
    {
        await _engine.StopAsync(reason);
        _clock.Stop();
        RefreshPlayback();
        if (notify) Notify("全部设备输出已归零。");
    }

    public async Task NavigateAsync(string page)
    {
        await StopAsync("离开工作台", false);
        _page = page;
        foreach (var property in new[] { nameof(IsWorkspace), nameof(IsWaves), nameof(IsHistory), nameof(IsSettings), nameof(PageTitle), nameof(PageSubtitle) }) Raise(property);
    }

    public void NewWave()
    {
        _editingId = null;
        EditorName = "我的新波形"; EditorDescription = "为自己的节奏命名";
        EditorShape = WaveShape.Sine; EditorPeriod = 4; EditorFrequency = 20;
        Notify("新波形编辑器已就绪。");
    }

    public void EditWave()
    {
        if (SelectedPreset is null) return;
        var preset = SelectedPreset.Preset;
        _editingId = SelectedPreset.IsBuiltIn ? null : preset.Id;
        EditorName = SelectedPreset.IsBuiltIn ? preset.Name + " · 副本" : preset.Name;
        EditorDescription = preset.Description; EditorShape = preset.Shape;
        EditorPeriod = preset.PeriodSeconds; EditorFrequency = preset.Frequency;
        EditorPoints = string.Join(", ", preset.Points?.Select(p => p.ToString("0.###", CultureInfo.InvariantCulture)) ?? ["0", "1", "0"]);
        Notify(SelectedPreset.IsBuiltIn ? "内置波形将另存为副本。" : "正在编辑所选波形。");
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
        Notify($"波形「{preset.Name}」已保存。");
    }

    private WaveformPreset BuildEditorPreset()
    {
        var points = EditorShape == WaveShape.Custom ? EditorPoints.Split([',', '，', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray() : null;
        return new(_editingId ?? Guid.NewGuid(), EditorName.Trim(), EditorShape,
            EditorPeriod, EditorFrequency, EditorDescription.Trim(), points);
    }

    public async Task DeleteWaveAsync()
    {
        if (SelectedPreset is null) return;
        if (SelectedPreset.IsBuiltIn) throw new InvalidOperationException("内置波形不能删除，可以创建副本。");
        var selected = SelectedPreset;
        await SaveAsync(Presets.Where(p => p != selected).Select(p => p.Preset));
        SelectedPreset = Presets[0]; Presets.Remove(selected); NewWave(); Notify("所选波形已删除。");
    }

    public async Task ImportAsync(string json)
    {
        var imported = WorkspaceStore.ImportPresets(json);
        var known = Presets.Select(p => p.Preset.Id).ToHashSet();
        var added = imported.Where(p => !known.Contains(p.Id)).ToArray();
        await SaveAsync(Presets.Select(p => p.Preset).Concat(added));
        foreach (var preset in added) Presets.Add(new(preset));
        Notify($"已导入 {added.Length} 个波形，跳过 {imported.Count - added.Length} 个重复项。");
    }

    public Task SaveAsync(IEnumerable<WaveformPreset>? presets = null) => _store.SaveAsync(new(1,
        (presets ?? Presets.Select(p => p.Preset)).Where(p => !WaveformPreset.BuiltIn.Any(b => b.Id == p.Id)).ToList(),
        _records.Take(500).ToList(), new(DefaultLimit, SessionMinutes, OffsetChannelB)));

    private void OnDeviceChanged(object? sender, EventArgs e) => RefreshDevices();
    private void OnSessionCompleted(object? sender, SessionRecord record)
    {
        _records.Insert(0, record); Sessions.Insert(0, new(record));
        while (Sessions.Count > 500) Sessions.RemoveAt(Sessions.Count - 1);
        while (_records.Count > 500) _records.RemoveAt(_records.Count - 1);
        Raise(nameof(SessionCount)); Raise(nameof(TotalTime));
        _ = GuardAsync(() => SaveAsync());
        Notify($"会话已保存 · {record.Reason}。");
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

    public void ShowError(Exception exception) { Status = exception.Message; IsError = true; Log(exception.Message); }
    public void Notify(string text) { Status = text; IsError = false; Log(text); }
    private void Log(string text)
    {
        Activities.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), text));
        while (Activities.Count > 20) Activities.RemoveAt(Activities.Count - 1);
    }
    private async Task GuardAsync(Func<Task> action) { try { await action(); } catch (Exception e) { ShowError(e); } }
    private static string FormatDuration(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await StopAsync("关闭应用", false);
            await SaveAsync();
        }
        finally
        {
            _controller.Changed -= OnDeviceChanged;
            _engine.Completed -= OnSessionCompleted;
            await _controller.DisposeAsync();
        }
    }
}
