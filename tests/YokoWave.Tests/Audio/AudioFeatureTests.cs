using YokoWave.Core;

namespace YokoWave.Tests.Audio;

public sealed class AudioFeatureTests
{
    [Fact]
    public void PcmAnalysisKeepsStereoLevelsAndSuppressesNonFiniteSamplesAndAmbientNoise()
    {
        var meter = new AudioFeatureAccumulator(16000);
        float[] samples = Enumerable.Range(0, 800).SelectMany(_ => new[] { .1f, .5f }).ToArray();
        meter.Process(samples, 2);
        Assert.Equal(-20, meter.Latest!.LeftDb, 5);
        Assert.Equal(-6.0206, meter.Latest.RightDb, 4);
        var envelope = new AudioEnvelope();
        var settings = new AudioSettings();
        Assert.Equal(0, envelope.Process(-90, 1, settings));
        Assert.True(envelope.Process(-56, 1, settings) > 0);
        Assert.True(envelope.Process(-59, 0, settings) > 0);
        Assert.Equal(0, envelope.Process(-61, 0, settings));
        Assert.Equal(0, envelope.Process(-59, 0, settings));
        meter.Reset();
        meter.Process(Enumerable.Repeat(float.NaN, 1600).ToArray(), 2);
        Assert.Equal(-120, meter.Latest!.LeftDb);
        meter.Reset();
        meter.Process(Enumerable.Range(0, 800).SelectMany(_ => new[] { 1f, float.NaN }).ToArray(), 2);
        Assert.Equal(0, meter.Latest!.LeftDb);
        Assert.Equal(-120, meter.Latest.RightDb);
        Assert.Equal(0, envelope.Process(double.NaN, 0, settings));
        Assert.True(new AudioEnvelope().Process(-20, 0, settings) > new AudioEnvelope().Process(-20, 0, settings with { Follow = AudioFollowMode.Rhythm }));
    }
}
