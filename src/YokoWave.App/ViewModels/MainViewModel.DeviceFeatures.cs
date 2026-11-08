using System.Diagnostics.CodeAnalysis;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class MainViewModel
{
    public ProtocolFeaturesViewModel ProtocolFeatures
    { get; private set; } = null!;
    private bool _featuresBusy;
    public bool CanReadDeviceFeatures => IsBluetooth && SelectedConnected && !_controller.IsDeviceBusy(SelectedDevice!.Id) && !_featuresBusy &&
        SelectedDevice?.Device.Profile?.Light == true;
    public bool HasDeviceFeatures => SelectedDevice?.Device.Profile?.Light == true;
    public bool CanControlLight => CanReadDeviceFeatures && _controller.Connections.TryGetValue(SelectedDevice?.Id ?? "", out var connection) &&
        connection.HardwareVersion is > 0 && connection.SoftwareVersion is > 0 && connection.LightEnabled.HasValue;
    public string FirmwareLabel
    {
        get
        {
            _controller.Connections.TryGetValue(SelectedDevice?.Id ?? "", out var connection);
            return L10n.Format("Device.Firmware", connection?.HardwareVersion?.ToString() ?? "—", connection?.SoftwareVersion?.ToString() ?? "—");
        }
    }
    private bool? SelectedLight => _controller.Connections.TryGetValue(SelectedDevice?.Id ?? "", out var connection) ? connection.LightEnabled : null;
    public string LightLabel => L10n.Get(SelectedLight switch { true => "Device.LightOn", false => "Device.LightOff", _ => "Device.LightUnknown" });
    public string LightAction => L10n.Get(SelectedLight == true ? "Device.TurnLightOff" : "Device.TurnLightOn");
    public AsyncCommand ReadDeviceFeaturesCommand
    { get; private set; }
    public AsyncCommand ToggleLightCommand
    { get; private set; }
    [MemberNotNull(nameof(ReadDeviceFeaturesCommand), nameof(ToggleLightCommand))]
    private void InitializeDeviceFeatureCommands()
    {
        ProtocolFeatures = new(_controller, ShowError);
        ReadDeviceFeaturesCommand = new(() => RunDeviceFeatureAsync(id => _controller.RefreshFeaturesAsync(id)), ShowError, () => CanReadDeviceFeatures);
        ToggleLightCommand = new(() => RunDeviceFeatureAsync(id => _controller.SetLightAsync(id, SelectedLight != true)), ShowError, () => CanControlLight);
    }
    private async Task RunDeviceFeatureAsync(Func<string, Task> action)
    {
        if (SelectedDevice is not { IsConnected: true } device || _featuresBusy || _controller.IsDeviceBusy(device.Id))
            return;
        _featuresBusy = true;
        RefreshDeviceFeatures();
        try
        {
            await action(device.Id);
        }
        finally
        {
            _featuresBusy = false;
            RefreshDeviceFeatures();
        }
    }
    private void RefreshDeviceFeatures()
    {
        ProtocolFeatures?.SelectDevice(SelectedDevice?.Device);
        foreach (var name in new[] { nameof(HasDeviceFeatures), nameof(CanReadDeviceFeatures), nameof(CanControlLight), nameof(FirmwareLabel), nameof(LightLabel), nameof(LightAction) })
            Raise(name);
        ReadDeviceFeaturesCommand?.NotifyCanExecuteChanged();
        ToggleLightCommand?.NotifyCanExecuteChanged();
    }
}
