using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class DevicesPaneViewModel : ObservableObject, IDisposable
{
    private readonly DeviceController _controller;
    private readonly Func<IDeviceTransport> _bluetoothFactory;
    private readonly Func<string, string, Task> _stopForDevice;
    private readonly Func<string, Task> _stopAll;
    private readonly Func<Guid, string?> _ownerName;
    private readonly Action<Exception> _error;
    private readonly Action<string, object?[]> _notify;
    private DeviceItem? _selected;
    private bool _scanning, _featuresBusy, _disposed;
    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public DeviceTelemetryViewModel Telemetry { get; }
    public ProtocolFeaturesViewModel ProtocolFeatures { get; }
    public RemoteSourcesViewModel RemoteSources { get; }
    public bool UsesLocalBluetooth => !_controller.IsLocalSimulation;
    public string TransportName => _controller.TransportName;
    public bool IsScanning
    {
        get => _scanning; private set
        {
            Set(ref _scanning, value);
            Raise(nameof(ScanLabel));
        }
    }
    public string ScanLabel => L10n.Get(IsScanning ? "Device.Scanning" : "Device.Scan");
    public int ConnectedCount => Devices.Count(d => d.IsConnected);
    public string ConnectedSummary => $"{ConnectedCount:00} / {Devices.Count:00}";
    public bool HasSelection => SelectedDevice is not null;
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
    private bool _showContacts = true;
    public bool ShowContacts
    {
        get => _showContacts; set
        {
            _showContacts = value;
            Raise(nameof(HasElectrodeReadings));
        }
    }
    public bool HasElectrodeReadings => ShowContacts && SelectedConnected && SelectedDevice?.Device.Profile?.Contacts == true;
    public string ReadingsLabel => L10n.Get(SelectedDevice?.Device.Simulated == true ? "Device.Readings" : "Device.HardwareReadings");
    private DeviceConnection? Connection => _controller.Connections.GetValueOrDefault(SelectedDevice?.Id ?? "");
    public bool HasDeviceFeatures => SelectedDevice?.Device.Profile?.Light == true;
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
    public async Task ScanAsync()
    {
        if (IsScanning)
            return;
        IsScanning = true;
        try
        {
            var found = await _controller.ScanAsync();
            foreach (var old in Devices.Where(d => !d.IsConnected && found.All(n => n.Id != d.Id)).ToArray())
                Devices.Remove(old);
            foreach (var device in found)
            {
                if (Devices.FirstOrDefault(d => d.Id == device.Id) is { } existing)
                    existing.UpdateReadings(device);
                else
                    Devices.Add(new(device, Devices.Count + 1, d => { SelectedDevice = d; return Task.CompletedTask; }, ToggleConnectionAsync, _error));
            }
            SelectedDevice ??= Devices.FirstOrDefault();
            Refresh();
            _notify(UsesLocalBluetooth ? "Status.BluetoothDiscovered" : "Status.Discovered", [found.Count]);
        }
        finally { IsScanning = false; }
    }
    public async Task ChangeTransportAsync(bool bluetooth)
    {
        if (bluetooth == UsesLocalBluetooth)
            return;
        if (IsScanning)
            throw new LocalizedOperationException("Error.BluetoothBusy");
        await _stopAll("Reason.TransportChanged");
        foreach (var id in _controller.Connections.Keys.ToArray())
            await _controller.DisconnectAsync(id);
        await _controller.ChangeTransportAsync(bluetooth ? _bluetoothFactory() : new SimulationTransport());
        SelectedDevice = null;
        Devices.Clear();
        Refresh();
    }
    public async Task ToggleConnectionAsync(DeviceItem device)
    {
        if (!device.IsConnected)
        {
            await _controller.ConnectAsync(device.Device);
            SelectedDevice = device;
            _notify("Status.Connected", [device.Name]);
        }
        else
        {
            List<Exception> errors = [];
            try
            {
                await _stopForDevice(device.Id, "Reason.ConnectionChanged");
            }
            catch (Exception error) { errors.Add(error); }
            try
            {
                await _controller.DisconnectAsync(device.Id);
            }
            catch (Exception error) { errors.Add(error); }
            Refresh();
            if (errors.Count > 0)
                throw new LocalizedAggregateException("Error.Cleanup", errors);
            _notify("Status.Disconnected", [device.Name]);
        }
        Refresh();
    }
    private async Task FeatureAsync(Func<string, Task> action)
    {
        if (SelectedDevice is not { IsConnected: true } device || _featuresBusy || _controller.IsDeviceBusy(device.Id))
            return;
        _featuresBusy = true;
        Refresh();
        try
        {
            await action(device.Id);
        }
        finally { _featuresBusy = false; Refresh(); }
    }
    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_disposed) Refresh(); });
    private void OnFault(object? sender, DeviceTransportFault fault) => Dispatcher.UIThread.Post(async () =>
    {
        if (_disposed)
            return;
        try
        {
            await _stopForDevice(fault.DeviceId, "Reason.ConnectionError");
        }
        catch (Exception cleanup) { _error(new LocalizedAggregateException("Error.Cleanup", [fault.Error, cleanup])); return; }
        _error(fault.Error);
    });
    public void Refresh()
    {
        foreach (var item in Devices)
        {
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
        foreach (var property in new[] { nameof(TransportName), nameof(UsesLocalBluetooth), nameof(ScanLabel), nameof(ConnectedCount), nameof(ConnectedSummary), nameof(HasSelection),
            nameof(SelectedName), nameof(SelectedKind), nameof(SelectedId), nameof(SelectedBatteryLabel), nameof(SelectedSignal), nameof(SelectedConnected), nameof(SelectedContacts),
            nameof(HasElectrodeReadings), nameof(DeviceHelp), nameof(ReadingsLabel), nameof(HasDeviceFeatures), nameof(CanReadDeviceFeatures), nameof(CanControlLight), nameof(FirmwareLabel), nameof(LightLabel), nameof(LightAction) })
            Raise(property);
        ReadDeviceFeaturesCommand.NotifyCanExecuteChanged();
        ToggleLightCommand.NotifyCanExecuteChanged();
    }
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
    public void Dispose()
    {
        _disposed = true;
        _controller.Changed -= OnChanged;
        _controller.Faulted -= OnFault;
        L10n.Current.PropertyChanged -= OnLanguage;
    }
}
