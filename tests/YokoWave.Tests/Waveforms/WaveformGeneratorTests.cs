using YokoWave.Core;

namespace YokoWave.Tests.Waveforms;

public sealed class WaveformGeneratorTests
{
    [Theory]
    [InlineData(WaveShape.Sine)]
    [InlineData(WaveShape.Triangle)]
    [InlineData(WaveShape.Pulse)]
    [InlineData(WaveShape.Ramp)]
    [InlineData(WaveShape.Custom)]
    public void WaveformsStayNormalizedAndRepeat(WaveShape shape)
    {
        var waveform = new WaveformAsset(Guid.NewGuid(), "test", shape, 4, 20, [0, 0.3, 1, 0]);
        for (int i = -100; i < 100; i++)
        {
            double time = i / 10d;
            double value = WaveformGenerator.Sample(waveform, time);
            Assert.InRange(value, 0, 1);
            Assert.Equal(value, WaveformGenerator.Sample(waveform, time + 4), 8);
        }
    }
}
