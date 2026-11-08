using Avalonia.Headless.XUnit;
using YokoWave.App.ViewModels;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using Xunit;

namespace YokoWave.Tests;

public sealed class ProtocolAuditTests
{
    [Theory]
    [InlineData(BluetoothProtocol.Opossum)]
    [InlineData(BluetoothProtocol.Civet)]
    public async Task FixedIndicatorProtocolsRejectUndocumentedColors(BluetoothProtocol protocol)
    {
        var adapter = new HardwareGattAdapter(protocol);
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        Assert.False(device.Profile!.Supports(DeviceCapability.IndicatorColor));
        await Assert.ThrowsAsync<LocalizedArgumentException>(() => transport.ExecuteCommandAsync(device.Id,
            new ConfigureSensorStream(true, 2), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LocalizedOperationException>(() => transport.ExecuteCommandAsync(device.Id,
            new ConfigureIndicator(2), TestContext.Current.CancellationToken));
        await transport.ExecuteCommandAsync(device.Id, new ConfigureSensorStream(false), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0x50, 1, 0 }, adapter.Links[device.Id].Writes[^1].Data[..3]);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(62, 120)]
    [InlineData(120, 3600)]
    public void PawRampTimingCoversDisabledSecondsAndMinutes(int code, int? seconds)
    {
        Assert.Equal(seconds, PawParameterTiming.Duration(code)?.TotalSeconds);
        var packet = PawProtocol.Configure(new(new PawRandomReaction(Dynamics: new(IncreaseTimeCode: code, DecreaseTimeCode: code))));
        Assert.Equal(code, packet[10]);
        Assert.Equal(code, packet[12]);
    }

    [Fact]
    public void V2DisabledPresetKeepsTheModeByteWithinTheProtocolRange()
    {
        var packet = V2Protocol.Fixed(new(100, 100, 20), new(PresetA: 0, PresetB: 16));
        Assert.Equal("3511010000010114106D", Convert.ToHexString(packet));
        Assert.All(new[] { packet[5], packet[8] }, mode => Assert.InRange(mode, (byte)1, (byte)16));
    }

    [Fact]
    public async Task OpossumPlaybackUsesSingleChannelHoldAndReassertsModeValuesAfterPhysicalChanges()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Opossum);
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        var link = adapter.Links[device.Id];
        await transport.SendAsync(device.Id, new(20, 0, 20), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xb3, 40, 255 }, link.Writes.Last(entry => entry.Data[0] == 0xb3).Data);
        var before = link.Writes.Count(entry => entry.Data[0] == 0xb3);
        await transport.SendAsync(device.Id, new(20, 0, 20), TestContext.Current.CancellationToken);
        Assert.Equal(before, link.Writes.Count(entry => entry.Data[0] == 0xb3));
        await transport.SendAsync(device.Id, new(20, 30, 20), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xb3, 255, 60 }, link.Writes.Last(entry => entry.Data[0] == 0xb3).Data);
        link.Notify([0xb3, 80, 120]);
        await transport.SendAsync(device.Id, new(20, 30, 20), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xb3, 40, 60 }, link.Writes.Last(entry => entry.Data[0] == 0xb3).Data);
    }

    [AvaloniaFact]
    public async Task PawFormsExposeCorrectColorsAndEveryRampTimeWithoutLosingValuesOnTranslation()
    {
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
        var previous = L10n.Current.Language;
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
            var packet = adapter.Links[device.Id].Writes[^1].Data;
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
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Paw, BluetoothProtocol.Opossum, BluetoothProtocol.YokonexV1);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var devices = await controller.ScanAsync(TestContext.Current.CancellationToken);
        foreach (var device in devices) await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        var telemetry = new DeviceTelemetryViewModel(controller, error => throw error);
        var paw = devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.Paw);
        adapter.Links[paw.Id].Notify([0xd0, 1, 1, 0, 0, 0, 0, 0, 8]);
        await Wait(() => controller.Connections[paw.Id].Telemetry?.VoltageRaw == 8);
        telemetry.SelectDevice(paw);
        Assert.Contains(telemetry.Readings, row => row.Label == L10n.Get("Telemetry.VoltageVolts") && row.Value == L10n.Format("Telemetry.Volts", .07));
        var opossum = devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.Opossum);
        adapter.Links[opossum.Id].Notify([0xd0, 5, 5, 0x22, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        await Wait(() => controller.Connections[opossum.Id].Telemetry?.Buttons == 0x2205);
        Assert.Equal(new[] { OpossumButton.Select1, OpossumButton.Home, OpossumButton.Down, OpossumButton.A }, OpossumProtocol.PressedButtons(0x2205));
        telemetry.SelectDevice(opossum);
        Assert.Contains(telemetry.Readings, row => row.Label == L10n.Get("Telemetry.ButtonNames") && row.Value.Contains("SEL 1", StringComparison.Ordinal));
        var electrical = devices.Single(device => device.Profile?.Protocol == BluetoothProtocol.YokonexV1);
        adapter.Links[electrical.Id].Notify(V1Protocol.WithChecksum([0x35, 0x71, 0x55, 3]));
        await Wait(() => controller.Connections[electrical.Id].Telemetry?.ProtocolErrorCode == 3);
        telemetry.SelectDevice(electrical);
        Assert.Contains(telemetry.Readings, row => row.Label == L10n.Get("Telemetry.ProtocolError") && row.Value == L10n.Format("Telemetry.ProtocolErrorValue", 3, L10n.Get("ProtocolError.3")));
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }
}
