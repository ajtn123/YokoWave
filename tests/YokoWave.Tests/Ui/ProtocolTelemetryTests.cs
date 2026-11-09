using Avalonia.Headless.XUnit;
using YokoWave.App.ViewModels;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Tests.Support.Bluetooth;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class ProtocolTelemetryTests
{
    [AvaloniaFact]
    public async Task PawFormsExposeCorrectColorsAndEveryRampTimeWithoutLosingValuesOnTranslation()
    {
        using var state = new UiTestState();
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Paw);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        var editor = new ProtocolFeaturesViewModel(controller, error => throw error);
        editor.SelectDevice(device);
        editor.SelectedCommand = editor.Commands.Single(option => option.Key == "PawRandom");
        var timing = editor.Parameters.Single(field => field.Label == L10n.Get("ProtocolField.IncreaseTimeCode"));
        var color = editor.Parameters.Single(field => field.Label == L10n.Get("ProtocolField.IndicatorColor"));
        Assert.Equal(121, timing.Choices.Count);
        timing.Selected = timing.Choices.Single(option => option.Value == 120);
        color.Selected = color.Choices.Single(option => option.Value == 3);
        Assert.Equal(6, color.Choices.Count);
        string previous = L10n.Current.Language;
        try
        {
            L10n.Current.SetLanguage("en");
            editor.RefreshLocalization();
            Assert.Equal("60 minutes", timing.Selected!.Label);
            Assert.Equal("Purple", color.Selected!.Label);
            L10n.Current.SetLanguage("zh-CN");
            editor.RefreshLocalization();
            Assert.Equal("60 分钟", timing.Selected.Label);
            Assert.Equal(120, timing.Selected.Value);
            await editor.ExecuteAsync();
            byte[] packet = adapter.Links[device.Id].Writes[^1].Data;
            Assert.Equal(3, packet[1]);
            Assert.Equal(120, packet[10]);
        }
        finally
        {
            L10n.Current.SetLanguage(previous);
        }
    }

    [AvaloniaFact]
    public async Task ReportedVoltageButtonsAndProtocolErrorsHaveDecodedReadings()
    {
        using var state = new UiTestState();
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Paw, BluetoothProtocol.Opossum, BluetoothProtocol.YokonexV1);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var devices = await controller.ScanAsync(TestContext.Current.CancellationToken);
        foreach (var device in devices)
            await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        var telemetry = new DeviceTelemetryViewModel(controller, error => throw error);
        var paw = devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.Paw);
        adapter.Links[paw.Id].Notify([0xd0, 1, 1, 0, 0, 0, 0, 0, 8]);
        await UiActions.EventuallyAsync(() => controller.Connections[paw.Id].Telemetry?.VoltageRaw == 8);
        telemetry.SelectDevice(paw);
        Assert.Contains(telemetry.Readings, row => row.Label == L10n.Get("Telemetry.VoltageVolts") && row.Value == L10n.Format("Telemetry.Volts", .07));
        var opossum = devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.Opossum);
        adapter.Links[opossum.Id].Notify([0xd0, 5, 5, 0x22, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        await UiActions.EventuallyAsync(() => controller.Connections[opossum.Id].Telemetry?.Buttons == 0x2205);
        Assert.Equal(new[] { OpossumButton.Select1, OpossumButton.Home, OpossumButton.Down, OpossumButton.A }, OpossumProtocol.PressedButtons(0x2205));
        telemetry.SelectDevice(opossum);
        Assert.Contains(telemetry.Readings, row => row.Label == L10n.Get("Telemetry.ButtonNames") && row.Value.Contains("SEL 1", StringComparison.Ordinal));
        var electrical = devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.YokonexV1);
        adapter.Links[electrical.Id].Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 3]));
        await UiActions.EventuallyAsync(() => controller.Connections[electrical.Id].Telemetry?.ProtocolErrorCode == 3);
        telemetry.SelectDevice(electrical);
        Assert.Contains(telemetry.Readings, row => row.Label == L10n.Get("Telemetry.ProtocolError") && row.Value == L10n.Format("Telemetry.ProtocolErrorValue", 3, L10n.Get("ProtocolError.3")));
    }
}
