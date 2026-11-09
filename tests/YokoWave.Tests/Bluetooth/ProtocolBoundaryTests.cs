using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Bluetooth;

public sealed class ProtocolBoundaryTests
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
        await Assert.ThrowsAsync<LocalizedArgumentException>(() => transport.ExecuteCommandAsync(device.Id, new ConfigureSensorStream(true, 2), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LocalizedOperationException>(() => transport.ExecuteCommandAsync(device.Id, new ConfigureIndicator(2), TestContext.Current.CancellationToken));
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
        byte[] packet = PawProtocol.Configure(new(new PawRandomReaction(Dynamics: new(IncreaseTimeCode: code, DecreaseTimeCode: code))));
        Assert.Equal(code, packet[10]);
        Assert.Equal(code, packet[12]);
    }

    [Fact]
    public void V2DisabledPresetKeepsTheModeByteWithinTheProtocolRange()
    {
        byte[] packet = V2Protocol.Fixed(new(100, 100, 20), new(PresetA: 0, PresetB: 16));
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
        int before = link.Writes.Count(entry => entry.Data[0] == 0xb3);
        await transport.SendAsync(device.Id, new(20, 0, 20), TestContext.Current.CancellationToken);
        Assert.Equal(before, link.Writes.Count(entry => entry.Data[0] == 0xb3));
        await transport.SendAsync(device.Id, new(20, 30, 20), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xb3, 255, 60 }, link.Writes.Last(entry => entry.Data[0] == 0xb3).Data);
        link.Notify([0xb3, 80, 120]);
        await transport.SendAsync(device.Id, new(20, 30, 20), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xb3, 40, 60 }, link.Writes.Last(entry => entry.Data[0] == 0xb3).Data);
    }
}
