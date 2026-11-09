using System.Buffers.Binary;
using System.Text.Json;
using Xunit;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Tests;

public sealed class GeneralWaveformTests
{
    [Theory]
    [InlineData("default-waveforms")]
    [InlineData("dglab-waveforms")]
    public void BuiltInResourcesUseTheSameFormatAsUserImports(string resource)
    {
        using var stream = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream($"YokoWave.Core.Data.{resource}.json");
        using var reader = new StreamReader(stream!);
        var imported = WaveformExchange.Import(reader.ReadToEnd());
        Assert.All(imported, wave => Assert.Equivalent(DefaultWaveformCatalog.Waveforms.Single(builtIn => builtIn.Id == wave.Id), wave, strict: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidWindowIsRejectedBeforeAnyDeviceWrite(bool missingSample)
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var transport = new BluetoothDeviceTransport(adapter);
        var device = Assert.Single(await transport.ScanAsync(TestContext.Current.CancellationToken));
        await transport.ConnectAsync(device, TestContext.Current.CancellationToken);
        int before = adapter.Links[device.Id].Writes.Count;
        WaveformSample[] window = missingSample ? [new(1, 20), null!, new(1, 20), new(1, 20)] : [new(1, 20)];
        var frame = new OutputFrame(20, 20, 20, WindowA: window);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => transport.SendAsync(device.Id, frame, TestContext.Current.CancellationToken));
        Assert.ThrowsAny<ArgumentException>(() => DgLabProtocol.V3(frame));
        Assert.Equal(before, adapter.Links[device.Id].Writes.Count);
    }

    [Fact]
    public void NormalizedJsonRoundTripsSignedChannelsWithoutAnyProtocolFields()
    {
        string json = WaveformExchange.Export(DefaultWaveformCatalog.Waveforms);
        foreach (string? oldField in new[] { "Family", "FrequencyCode", "PulseWidthCode", "Motors", "IsValid", "PreviewLevel" })
            Assert.DoesNotContain($"\"{oldField}\"", json);
        Assert.Equivalent(DefaultWaveformCatalog.Waveforms, WaveformExchange.Import(json), strict: true);
        var signed = DefaultWaveformCatalog.Waveforms.SelectMany(wave => wave.Samples!).First(sample => sample.Channels?.Any(value => value < 0) == true);
        Assert.Contains(signed.Channels!, value => value < 0);
        using var document = JsonDocument.Parse(json);
        Assert.All(document.RootElement.EnumerateArray(), wave => Assert.Equal(1, wave.GetProperty("FormatVersion").GetInt32()));
    }

    [Fact]
    public void DeviceConversionUsesPhysicalFrequencyAndNormalizedWidthAndRectifiesUnsupportedDirection()
    {
        var sample = new WaveformSample(.5, 20, .8, [-1, .5, .25]);
        var frame = new OutputFrame(40, 50, 20, sample, ChannelC: 60, OutputLimit: 30);
        byte[] packet = V2Protocol.Encode(frame);
        Assert.Equal(41, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(3, 2)));
        Assert.Equal(new byte[] { 20, 80 }, packet[5..7]);
        Assert.Equal(new byte[] { 20, 40 }, packet[9..11]);
        byte[] v3 = DgLabProtocol.V3(frame);
        Assert.Equal(new byte[] { 30, 30 }, v3[2..4]);
        Assert.Equal((byte)50, v3[4]);
        Assert.Equal((byte)80, v3[8]);
        Assert.Equal((byte)40, v3[16]);
        byte[] motors = YokonexToyProtocol.MotorOutput(frame, 7);
        Assert.Equal(new byte[] { 2, 1, 0 }, motors[2..5]);
        Assert.Equal(new double[] { -1, .5, .25 }, sample.Channels);
    }

    [Fact]
    public void ModernPacketsPreserveFourEnvelopeSamplesWithoutExceedingTheModeLimit()
    {
        WaveformSample[] samples = [new(0, 20), new(.25, 20), new(.5, 20), new(1, 20)];
        var frame = new OutputFrame(90, 90, 20, WindowA: samples, WindowB: samples, OutputLimit: 30);
        byte[] packet = DgLabProtocol.V3(frame);
        Assert.Equal(new byte[] { 60, 60 }, packet[2..4]);
        Assert.Equal(new byte[] { 0, 25, 50, 100 }, packet[8..12]);
        Assert.Equal(new byte[] { 0, 25, 50, 100 }, DgLabProtocol.OpossumWave(frame)[8..12]);
        var clipped = WaveformRendering.WithRemaining(frame, .05);
        Assert.Equal(new byte[] { 0, 100, 0, 0 }, DgLabProtocol.V3(clipped)[8..12]);
        var wave = new WaveformAsset(Guid.NewGuid(), "Once", WaveShape.Custom, .2, 20, [1, 1]);
        Assert.Equal(new double[] { 1, 0, 0, 0 }, WaveformGenerator.Window(wave, .175, repeat: false).Select(sample => sample.Level));
    }

    [Theory]
    [InlineData("[{\"Id\":\"78bbc211-e75c-40cd-b250-08c0fedb3e02\",\"Name\":\"Old\",\"Shape\":\"Sampled\",\"PeriodSeconds\":1,\"Frequency\":20,\"Samples\":[{\"Level\":1,\"FrequencyCode\":20,\"PulseWidthCode\":100}]}]")]
    [InlineData("[{\"FormatVersion\":1,\"Id\":\"78bbc211-e75c-40cd-b250-08c0fedb3e02\",\"Name\":\"Missing\",\"Shape\":\"Sampled\",\"PeriodSeconds\":1,\"Frequency\":20,\"Samples\":[{\"Level\":1,\"FrequencyHz\":20}]}]")]
    public void ImportRejectsOldOrIncompleteSamplesInsteadOfSilentlyConvertingThem(string json) =>
        Assert.Throws<JsonException>(() => WaveformExchange.Import(json));
}
