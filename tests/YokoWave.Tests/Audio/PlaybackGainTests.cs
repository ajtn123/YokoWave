using YokoWave.Core;

namespace YokoWave.Tests.Audio;

public sealed class PlaybackGainTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(100)]
    public void SpeakerGainDoesNotChangeTheFeaturesUsedForDeviceOutput(double volume)
    {
        float[] samples = Enumerable.Repeat(0.5f, 4800).ToArray();
        var accumulator = new AudioFeatureAccumulator();
        accumulator.Process(samples, 2);
        var feature = accumulator.Latest;
        Assert.NotNull(feature);
        new AudioPlaybackGain(volume).Apply(samples);
        Assert.All(samples, sample => Assert.Equal(0.5 * volume / 100, sample, 6));
        Assert.Same(feature, accumulator.Latest);
        Assert.True(feature.LeftDb > -7);
    }

    [Fact]
    public void MuteClearsEveryChannelIncludingNonFiniteSamples()
    {
        float[] samples = [1, -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.5f, -0.5f];
        new AudioPlaybackGain(0).Apply(samples);
        Assert.All(samples, sample => Assert.Equal(0, sample));
        Assert.Equal(0, new AudioPlaybackGain(double.NaN).Apply(1));
        Assert.Equal(0, new AudioPlaybackGain(-1).Apply(1));
        Assert.Equal(1, new AudioPlaybackGain(101).Apply(1));
    }
}
