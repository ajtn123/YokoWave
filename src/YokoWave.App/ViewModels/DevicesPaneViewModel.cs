using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class DevicesPaneViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly Func<IDeviceTransport> _bluetoothFactory;
    private readonly Func<string, string, Task> _stopForDevice;
    private readonly Func<string, Task> _stopAll;
    private readonly Func<Guid, string?> _ownerName;
    private readonly Action<Exception> _error;
    private readonly Action<string, object?[]> _notify;
    private readonly Func<Task> _save;
    private readonly DeviceAutoConnector _autoConnector;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _monitor;
    private Task _preferenceSave = Task.CompletedTask;
    private DeviceItem? _selected;
    private bool _showContacts = true;
    private bool _scanning;
    private bool _featuresBusy;
    private bool _disposed;
    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public bool AutoConnect
    {
        get => _autoConnector.Enabled;
        set
        {
            if (_disposed || value == _autoConnector.Enabled)
                return;
            _autoConnector.Enabled = value;
            Raise();
            _preferenceSave = SavePreferencesAsync(_preferenceSave);
        }
    }
    public List<string> AutoConnectDevices => _autoConnector.RememberedDevices.ToList();
    public List<DeviceOutputSettings> OutputSettings => _controller.OutputSettings.ToList();
    public DeviceTelemetryViewModel Telemetry { get; }
    public ProtocolFeaturesViewModel ProtocolFeatures { get; }
    public RemoteSourcesViewModel RemoteSources { get; }
    public bool UsesLocalBluetooth => !_controller.IsLocalSimulation;
    public string TransportName => _controller.TransportName;
    public bool IsScanning
    {
        get => _scanning;
        private set
        {
            Set(ref _scanning, value);
            Raise(nameof(ScanLabel));
            ScanCommand.NotifyCanExecuteChanged();
        }
    }
    public string ScanLabel => L10n.Get(IsScanning ? "Device.Scanning" : "Device.Scan");
    public int ConnectedCount => Devices.Count(d => d.IsConnected);
    public string ConnectedSummary => $"{ConnectedCount:00} / {Devices.Count:00}";
    public bool HasSelection => SelectedDevice is not null;
    public ObservableCollection<DeviceHardwareOptionsViewModel> HardwareSelection { get; } = [];
    public bool SelectedConnected => SelectedDevice?.IsConnected == true;
    public string SelectedName => SelectedDevice?.Name ?? L10n.Get("Device.None");
    public string SelectedKind => SelectedDevice?.Kind ?? L10n.Get("Device.ConnectHelp");
    public string SelectedId => SelectedDevice?.Id ?? L10n.Get("Device.NoneId");
    public string SelectedBatteryLabel => L10n.Format("Device.Battery", SelectedDevice?.Battery ?? "—");
    public string SelectedSignal => SelectedDevice?.Signal ?? "—";
    public string DeviceHelp => L10n.Get(SelectedDevice?.Device.Origin == DeviceOrigin.Remote ? "Remote.DeviceHelp" :
        (SelectedDevice?.Device.Simulated ?? _controller.IsLocalSimulation) ? "Device.SimulationHelp" : "Device.BluetoothHelp");
    public string SelectedContacts => L10n.Format("Device.Contacts", L10n.Get("Contact." + (Connection?.ContactA ?? ElectrodeContact.Unknown)),
        L10n.Get("Contact." + (Connection?.ContactB ?? ElectrodeContact.Unknown)));
    public bool ShowContacts
    {
        get => _showContacts;
        set
        {
            _showContacts = value;
            Raise(nameof(HasElectrodeReadings));
        }
    }
    public bool HasElectrodeReadings => ShowContacts && SelectedConnected && SelectedDevice?.Device.Profile?.Contacts == true;
    public string ReadingsLabel => L10n.Get(SelectedDevice?.Device.Simulated == true ? "Device.Readings" : "Device.HardwareReadings");
    private DeviceConnection? Connection => _controller.Connections.GetValueOrDefault(SelectedDevice?.Id ?? "");
    public bool HasDeviceFeatures => SelectedDevice?.Device.Supports(Core.Bluetooth.DeviceCapability.Light) == true;
    public bool CanReadDeviceFeatures => SelectedConnected && HasDeviceFeatures && !_featuresBusy && !_controller.IsDeviceBusy(SelectedDevice!.Id);
    public bool CanControlLight => CanReadDeviceFeatures && Connection is { HardwareVersion: > 0, SoftwareVersion: > 0, LightEnabled: not null };
    public string FirmwareLabel => L10n.Format("Device.Firmware", Connection?.HardwareVersion?.ToString() ?? "—", Connection?.SoftwareVersion?.ToString() ?? "—");
    public string LightLabel => L10n.Get(Connection?.LightEnabled switch { true => "Device.LightOn", false => "Device.LightOff", _ => "Device.LightUnknown" });
    public string LightAction => L10n.Get(Connection?.LightEnabled == true ? "Device.TurnLightOff" : "Device.TurnLightOn");
    public DeviceItem? SelectedDevice
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            if (_selected is { } previous)
                previous.IsSelected = false;
            if (!Set(ref _selected, value))
                return;
            if (value is not null)
                value.IsSelected = true;
            Refresh();
        }
    }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand ReadDeviceFeaturesCommand { get; }
    public AsyncCommand ToggleLightCommand { get; }
    public DevicesPaneViewModel(DeviceController controller, Func<IDeviceTransport> bluetoothFactory, Func<Task> save,
        Func<string, string, Task> stopForDevice, Func<string, Task> stopAll, Func<Guid, string?> ownerName,
        Action<Exception> error, Action<string, object?[]> notify)
    {
        _controller = controller;
        _bluetoothFactory = bluetoothFactory;
        _stopForDevice = stopForDevice;
        _stopAll = stopAll;
        _ownerName = ownerName;
        _error = error;
        _notify = notify;
        _save = save;
        _autoConnector = new(controller, id => _stopForDevice(id, "Reason.ConnectionChanged"), device =>
        {
            Refresh();
            if (!_disposed)
                _notify("Status.Connected", [device.Name]);
        }, error =>
        {
            if (!_disposed)
                _error(error);
        });
        Telemetry = new(controller, error);
        ProtocolFeatures = new(controller, error);
        RemoteSources = new(controller, ScanAsync, save, id => stopForDevice(id, "Reason.ConnectionChanged"), error);
        ScanCommand = new(ScanAsync, error, () => !IsScanning);
        ReadDeviceFeaturesCommand = new(() => FeatureAsync(id => controller.RefreshFeaturesAsync(id)), error, () => CanReadDeviceFeatures);
        ToggleLightCommand = new(() => FeatureAsync(id => controller.SetLightAsync(id, Connection?.LightEnabled != true)), error, () => CanControlLight);
        controller.Changed += OnChanged;
        controller.Faulted += OnFault;
        L10n.Current.PropertyChanged += OnLanguage;
    }
    public void LoadConnectionPreferences(AppSettings settings)
    {
        _autoConnector.Load(settings.AutoConnect, settings.AutoConnectDevices ?? []);
        _controller.LoadOutputSettings(settings.DeviceOutputs ?? []);
        Raise(nameof(AutoConnect));
    }
    public void StartAutoConnect() => _monitor ??= MonitorConnectionsAsync();
    public async Task FlushPreferencesAsync()
    {
        Task pending;
        do
        {
            pending = _preferenceSave;
            await pending;
        } while (pending != _preferenceSave);
    }
    public Task ScanAsync() => ScanCoreAsync(automatic: false);
    public async Task ChangeTransportAsync(bool bluetooth)
    {
        if (_disposed || bluetooth == UsesLocalBluetooth)
            return;
        if (IsScanning)
            throw new LocalizedOperationException("Error.BluetoothBusy");
        await _operations.WaitAsync(_lifetime.Token);
        try
        {
            await _stopAll("Reason.TransportChanged");
            foreach (string? id in _controller.Connections.Keys.ToArray())
                await _controller.DisconnectAsync(id, _lifetime.Token);
            await _controller.ChangeTransportAsync(bluetooth ? _bluetoothFactory() : new SimulationTransport());
            SelectedDevice = null;
            Devices.Clear();
            Refresh();
        }
        finally
        {
            _operations.Release();
        }
    }
    public async Task ToggleConnectionAsync(DeviceItem device)
    {
        if (_disposed)
            return;
        bool disconnect = device.IsConnected;
        if (disconnect)
            _autoConnector.Forget(device.Id);
        await _operations.WaitAsync(_lifetime.Token);
        try
        {
            if (!disconnect)
            {
                await _controller.ConnectAsync(device.Device, _lifetime.Token);
                _autoConnector.Remember(device.Id);
                SelectedDevice = device;
                await _save();
                _notify("Status.Connected", [device.Name]);
            }
            else
            {
                List<Exception> errors = [];
                try
                {
                    await _stopForDevice(device.Id, "Reason.ConnectionChanged");
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                try
                {
                    await _controller.DisconnectAsync(device.Id, _lifetime.Token);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                try
                {
                    await _save();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
                Refresh();
                if (errors.Count > 0)
                    throw new LocalizedAggregateException("Error.Cleanup", errors);
                _notify("Status.Disconnected", [device.Name]);
            }
        }
        finally
        {
            _operations.Release();
            Refresh();
        }
    }
    public void Refresh()
    {
        var hardware = SelectedDevice?.HardwareOptions is { HasOptions: true } options ? options : null;
        if ((HardwareSelection.FirstOrDefault() != hardware) || (hardware is null && HardwareSelection.Count > 0))
        {
            HardwareSelection.Clear();
            if (hardware is not null)
                HardwareSelection.Add(hardware);
        }
        foreach (var item in Devices)
        {
            item.HardwareOptions?.Refresh();
            var connection = _controller.Connections.GetValueOrDefault(item.Id);
            item.IsConnected = connection is not null;
            item.OutputA = connection is null ? 0 : WaveformRendering.Strength(connection.Output, 0);
            item.OutputB = connection is null ? 0 : WaveformRendering.Strength(connection.Output, 1);
            item.OutputC = connection is null ? 0 : WaveformRendering.Strength(connection.Output, 2);
            item.ActivePipelineName = _controller.DeviceOwner(item.Id) is { } owner ? _ownerName(owner) : null;
            if (connection is not null)
                item.UpdateReadings(connection.Device);
        }
        Telemetry.SelectDevice(SelectedDevice?.Device);
        ProtocolFeatures.SelectDevice(SelectedDevice?.Device);
        foreach (string? property in new[] { nameof(TransportName), nameof(UsesLocalBluetooth), nameof(ScanLabel), nameof(ConnectedCount), nameof(ConnectedSummary), nameof(HasSelection),
            nameof(SelectedName), nameof(SelectedKind), nameof(SelectedId), nameof(SelectedBatteryLabel), nameof(SelectedSignal), nameof(SelectedConnected), nameof(SelectedContacts),
            nameof(HasElectrodeReadings), nameof(DeviceHelp), nameof(ReadingsLabel), nameof(HasDeviceFeatures), nameof(CanReadDeviceFeatures), nameof(CanControlLight), nameof(FirmwareLabel), nameof(LightLabel), nameof(LightAction) })
        {
            Raise(property);
        }

        ReadDeviceFeaturesCommand.NotifyCanExecuteChanged();
        ToggleLightCommand.NotifyCanExecuteChanged();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        List<Exception> errors = [];
        try
        {
            await _lifetime.CancelAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        try
        {
            if (_monitor is not null)
                await _monitor;
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        await _operations.WaitAsync();
        _operations.Release();
        try
        {
            await RemoteSources.DisposeAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        try
        {
            await FlushPreferencesAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        _controller.Changed -= OnChanged;
        _controller.Faulted -= OnFault;
        L10n.Current.PropertyChanged -= OnLanguage;
        _operations.Dispose();
        _lifetime.Dispose();
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
    private Task SaveOutputSettingsAsync(DeviceDescriptor device, DeviceOutputSettings settings) =>
        _preferenceSave = SavePreferencesAsync(_preferenceSave, () => _controller.SetOutputSettingsAsync(device, settings));
    private async Task SavePreferencesAsync(Task previous, Func<Task>? update = null)
    {
        await previous;
        try
        {
            if (update is not null)
                await update();
            await _save();
        }
        catch (Exception error)
        {
            if (_disposed)
                throw;
            _error(error);
        }
    }
    private async Task MonitorConnectionsAsync()
    {
        bool reported = false;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _controller.Clock, _lifetime.Token);
                if (!_autoConnector.NeedsDiscovery || IsScanning)
                    continue;
                try
                {
                    await ScanCoreAsync(automatic: true);
                    reported = false;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    if (!reported && !_disposed)
                        _error(error);
                    reported = true;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private async Task ScanCoreAsync(bool automatic)
    {
        if (_disposed || IsScanning)
            return;
        await _operations.WaitAsync(_lifetime.Token);
        try
        {
            IsScanning = true;
            var found = await _controller.ScanAsync(_lifetime.Token);
            foreach (var old in Devices.Where(d => !d.IsConnected && found.All(n => n.Id != d.Id)).ToArray())
                Devices.Remove(old);
            foreach (var device in found)
            {
                if (Devices.FirstOrDefault(d => d.Id == device.Id) is { } existing)
                {
                    existing.UpdateReadings(device);
                }
                else
                {
                    Devices.Add(new(device, Devices.Count + 1, d =>
                    {
                        SelectedDevice = d;
                        return Task.CompletedTask;
                    }, ToggleConnectionAsync, _error)
                    {
                        HardwareOptions = new(device, _controller, () => !_disposed && !_controller.IsDeviceBusy(device.Id), settings => SaveOutputSettingsAsync(device, settings), _error)
                    });
                }
            }
            SelectedDevice ??= Devices.FirstOrDefault();
            Refresh();
            if (!automatic)
                _notify(UsesLocalBluetooth ? "Status.BluetoothDiscovered" : "Status.Discovered", [found.Count]);
            await _autoConnector.ConnectAvailableAsync(found, _lifetime.Token);
            Refresh();
        }
        finally
        {
            _operations.Release();
            IsScanning = false;
        }
    }
    private async Task FeatureAsync(Func<string, Task> action)
    {
        if (_disposed || SelectedDevice is not { IsConnected: true } device || _featuresBusy || _controller.IsDeviceBusy(device.Id))
            return;
        _featuresBusy = true;
        Refresh();
        try
        {
            await action(device.Id);
        }
        finally
        {
            _featuresBusy = false;
            Refresh();
        }
    }
    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
            Refresh();
    });
    private void OnFault(object? sender, DeviceTransportFault fault) => Dispatcher.UIThread.Post(async () =>
    {
        if (_disposed)
            return;
        try
        {
            await _stopForDevice(fault.DeviceId, "Reason.ConnectionError");
        }
        catch (Exception cleanup)
        {
            _error(new LocalizedAggregateException("Error.Cleanup", [fault.Error, cleanup]));
            return;
        }
        _error(fault.Error);
    });
    private void OnLanguage(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language))
            return;
        foreach (var device in Devices)
            device.RefreshLocalization();
        ProtocolFeatures.RefreshLocalization();
        RemoteSources.RefreshLocalization();
        Refresh();
    }
}
