using System.ComponentModel;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly DevicesPaneViewModel _devices;
    private readonly Func<bool> _busy;
    private readonly Action _timerChanged;
    private readonly Func<Task> _save;
    private readonly Action<Exception> _error;
    private double _defaultLimit = 60, _timerLimit = 120;
    private LanguageOption? _language;
    private TransportOption? _transport;
    public IReadOnlyList<LanguageOption> Languages { get; } = [new("en", L10n.Current.English["Language.en"]), new("zh-CN", L10n.Current.English["Language.zh-CN"])];
    public IReadOnlyList<TransportOption> TransportOptions { get; } = [new(false), new(true)];
    public LanguageOption? SelectedLanguage
    {
        get => _language;
        set
        {
            if (value is null || !Set(ref _language, value))
                return;
            L10n.Current.SetLanguage(value.Code);
            _ = SaveLanguageAsync();
        }
    }
    public TransportOption? SelectedTransport { get => _transport; set => Set(ref _transport, value); }
    public double DefaultLimit
    {
        get => _defaultLimit; set
        {
            if (double.IsFinite(value))
                Set(ref _defaultLimit, Math.Clamp(value, 0, 100));
        }
    }
    public double TimerLimitMinutes
    {
        get => _timerLimit;
        set
        {
            if (_busy() || !double.IsFinite(value) || !Set(ref _timerLimit, Math.Clamp(Math.Truncate(value), 1, 120)))
                return;
            _timerChanged();
        }
    }
    public bool ShowContacts
    {
        get => _devices.ShowContacts; set
        {
            _devices.ShowContacts = value;
            Raise();
        }
    }
    public bool IsAnyRunning => _busy();
    public string StoragePath { get; }
    public string DriverLabel => L10n.Format("Settings.DriverFormat", _devices.TransportName);
    public string TransportHelp => L10n.Get(_devices.UsesLocalBluetooth ? "Settings.BluetoothHelp" : "Settings.SimulationHelp");
    public RemoteSourcesViewModel RemoteSources => _devices.RemoteSources;
    public AsyncCommand SaveSettingsCommand { get; }
    public SettingsViewModel(string directory, DevicesPaneViewModel devices, Func<bool> busy, Action timerChanged, Func<Task> save, Action<Exception> error)
    {
        StoragePath = directory;
        _devices = devices;
        _busy = busy;
        _timerChanged = timerChanged;
        _save = save;
        _error = error;
        SaveSettingsCommand = new(async () =>
        {
            await _devices.ChangeTransportAsync(SelectedTransport?.UseBluetooth == true);
            await save();
            await _devices.ScanAsync();
            Refresh();
        }, error, () => !_devices.IsScanning);
        L10n.Current.PropertyChanged += OnLanguage;
    }
    public void Load(AppSettings settings)
    {
        ShowContacts = settings.ShowContacts;
        _defaultLimit = settings.DefaultLimit;
        _timerLimit = settings.TimerLimitMinutes;
        _language = Languages.Single(l => l.Code == JsonLocalizer.NormalizeLanguage(settings.Language ?? JsonLocalizer.SystemLanguage));
        _transport = TransportOptions.Single(t => t.UseBluetooth == settings.UseBluetooth);
        L10n.Current.SetLanguage(_language.Code);
        RemoteSources.Load(settings.RemoteEndpoints ?? []);
        foreach (var property in new[] { nameof(DefaultLimit), nameof(TimerLimitMinutes), nameof(SelectedLanguage), nameof(SelectedTransport) })
            Raise(property);
    }
    public AppSettings Snapshot() => new(DefaultLimit, SelectedLanguage?.Code, _devices.UsesLocalBluetooth, (int)TimerLimitMinutes, RemoteSources.ToSettings(), ShowContacts);
    public void Refresh()
    {
        Raise(nameof(IsAnyRunning));
        Raise(nameof(DriverLabel));
        Raise(nameof(TransportHelp));
        SaveSettingsCommand.NotifyCanExecuteChanged();
    }
    private void OnLanguage(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language))
            return;
        _language = Languages.Single(l => l.Code == L10n.Current.Language);
        Raise(nameof(SelectedLanguage));
        foreach (var transport in TransportOptions)
            transport.RefreshLocalization();
        Refresh();
    }
    private async Task SaveLanguageAsync()
    {
        try
        {
            await _save();
        }
        catch (Exception error) { _error(error); }
    }
    public void Dispose() => L10n.Current.PropertyChanged -= OnLanguage;
}
