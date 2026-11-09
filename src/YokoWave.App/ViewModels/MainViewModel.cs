using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

/// <summary>Composes independent pages, coordinates persistence and owns application shutdown.</summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly WorkspaceStore _store;
    private string _page = "pipelines";
    private bool _initialized, _disposed, _isError;
    private LocalizedMessage _status = new("Status.NoDevice", []);
    private Exception? _error;
    public DevicesPaneViewModel Devices { get; }
    public SettingsViewModel Settings { get; }
    public WaveformLibraryViewModel Waveforms { get; }
    public HistoryViewModel History { get; } = new();
    public PipelinesWorkspaceViewModel Pipelines { get; }
    public string Status => _error?.Message ?? _status.Value;
    public bool IsError
    {
        get => _isError; private set => Set(ref _isError, value);
    }
    public bool IsWorkspace => _page == "devices";
    public bool IsPipelines => _page == "pipelines";
    public bool IsWaves => _page == "waves";
    public bool IsHistory => _page == "history";
    public bool IsSettings => _page == "settings";
    public bool IsHelp => _page == "help";
    public string PageTitle => L10n.Get(_page switch
    {
        "devices" => "Page.Devices",
        "waves" => "Page.Waves",
        "history" => "Page.History",
        "settings" => "Page.Settings",
        "help" => "Page.Help",
        _ => "Pipeline.Title"
    });
    public ICommand WorkspaceCommand { get; }
    public ICommand PipelinesCommand { get; }
    public ICommand WavesCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand HelpCommand { get; }
    public AsyncCommand EmergencyStopCommand { get; }

    public MainViewModel(DeviceController controller, WorkspaceStore store, Func<IDeviceTransport>? bluetoothFactory = null, IAudioInputFactory? audioInputFactory = null)
    {
        _controller = controller;
        _store = store;
        var audio = audioInputFactory ?? new UnavailableAudioInputFactory();
        Devices = new(controller, bluetoothFactory ?? (() => new BluetoothDeviceTransport(new UnsupportedBluetoothAdapter())), SaveAsync,
            (id, reason) => Pipelines!.StopForDeviceAsync(id, reason), reason => StopAllAsync(reason),
            owner => Pipelines!.Pipelines.FirstOrDefault(p => p.Session.OwnerId == owner)?.Name, ShowError, NotifyArguments);
        Settings = new(store.DirectoryPath, Devices, () => Pipelines!.IsAnyRunning, () => Pipelines!.RefreshTimerLimit(), SaveAsync, ShowError);
        Waveforms = new(presets => SaveAsync(presets), id => Pipelines!.UsesWaveform(id), () => Pipelines!.RefreshWaveforms(), async waveform =>
        {
            if (Pipelines!.Selected?.UseWaveform(waveform) != true)
                throw new LocalizedOperationException("Error.PlaylistWave");
            await NavigateAsync("pipelines");
        }, () => Pipelines?.Selected?.CanUseWaveform == true, NotifyArguments, ShowError);
        Pipelines = new(controller, store.DirectoryPath, Waveforms.Presets, ShowError, AddSession,
            () => Settings.TimerLimitMinutes * 60, () => Settings.DefaultLimit / 100, audio, Devices);
        Pipelines.PropertyChanged += OnPipelinesChanged;
        Pipelines.WaveformCreated += (_, waveform) => _ = GuardAsync(() => Waveforms.AddAsync(waveform));
        WorkspaceCommand = new ActionCommand(() => _ = NavigateAsync("devices"));
        PipelinesCommand = new ActionCommand(() => _ = NavigateAsync("pipelines"));
        WavesCommand = new ActionCommand(() => _ = NavigateAsync("waves"));
        HistoryCommand = new ActionCommand(() => _ = NavigateAsync("history"));
        SettingsCommand = new ActionCommand(() => _ = NavigateAsync("settings"));
        HelpCommand = new ActionCommand(() => _ = NavigateAsync("help"));
        EmergencyStopCommand = new(() => StopAllAsync("Reason.AllStopped"), ShowError);
        L10n.Current.PropertyChanged += OnLanguageChanged;
    }
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        var data = await _store.LoadAsync();
        Settings.Load(data.Settings);
        Waveforms.Load(data.Presets);
        History.Load(data.Sessions, data.Statistics ?? new());
        await Devices.ChangeTransportAsync(data.Settings.UseBluetooth);
        await Pipelines.LoadAsync();
        _initialized = true;
        await Devices.ScanAsync();
        if (_store.LoadWarning is { } warning)
            ShowError(new IOException(warning));
    }
    public Task NavigateAsync(string page)
    {
        _page = page;
        foreach (var property in new[] { nameof(IsWorkspace), nameof(IsPipelines), nameof(IsWaves), nameof(IsHistory), nameof(IsSettings), nameof(IsHelp), nameof(PageTitle) })
            Raise(property);
        Waveforms.RefreshCommands();
        return Task.CompletedTask;
    }
    public Task StopAllAsync(string reason = "Reason.AllStopped") => Pipelines.StopAllAsync(reason);
    public Task SaveAsync() => SaveAsync(null);
    public async Task SaveAsync(IReadOnlyList<WaveformPreset>? assets)
    {
        if (!_initialized || Waveforms.Presets.Count == 0)
            return;
        await _store.SaveAsync(new(WorkspaceData.CurrentSchema,
            (assets ?? Waveforms.Presets.Select(p => p.Preset).ToArray()).Where(p => !DefaultWaveformCatalog.Contains(p.Id)).ToList(),
            History.Records.ToList(), Settings.Snapshot(), History.Statistics));
        History.Retain(_store.Journal, _store.Statistics);
    }
    private void AddSession(SessionRecord record)
    {
        History.Add(record);
        _ = GuardAsync(SaveAsync);
        Notify("Status.SessionSaved", new StoredReason(record.Reason));
    }
    private void OnPipelinesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
            return;
        Settings.Refresh();
        Waveforms.RefreshCommands();
        Devices.Refresh();
    }
    private void NotifyArguments(string key, object?[] arguments) => Notify(key, arguments);
    public void Notify(string key, params object?[] arguments)
    {
        _error = null;
        _status = new(key, arguments);
        IsError = false;
        Raise(nameof(Status));
        Log(_status);
    }
    public void ShowError(Exception exception)
    {
        Trace.TraceError("{0}", exception);
        _error = exception;
        _status = exception is ILocalizedError localized ? localized.LocalizedMessage : new(exception.Message, [], true);
        IsError = true;
        Raise(nameof(Status));
        Log(_status);
    }
    private void Log(LocalizedMessage message)
    {
        History.Log(message);
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language))
            return;
        Raise(nameof(PageTitle));
        Raise(nameof(Status));
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        List<Exception> errors = [];
        try
        {
            await Pipelines.DisposeAsync();
        }
        catch (Exception error) { errors.Add(error); }
        try
        {
            await SaveAsync();
        }
        catch (Exception error) { errors.Add(error); }
        Pipelines.PropertyChanged -= OnPipelinesChanged;
        L10n.Current.PropertyChanged -= OnLanguageChanged;
        Devices.Dispose();
        Settings.Dispose();
        Waveforms.Dispose();
        History.Dispose();
        try
        {
            await _controller.DisposeAsync();
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
