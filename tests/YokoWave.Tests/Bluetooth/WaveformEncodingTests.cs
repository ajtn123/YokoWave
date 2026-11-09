using System.Buffers.Binary;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Tests.Bluetooth;

public sealed class WaveformEncodingTests
{
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
}
