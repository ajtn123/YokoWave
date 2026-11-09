using YokoWave.Core;

namespace YokoWave.Tests.Support.Pipelines;

internal static class WaveformTestAssets
{
    public static WaveformAsset CreateThreeChannelWaveform() => new(Guid.NewGuid(), "Three channels", WaveShape.Sampled, .2, 20,
        Samples: [new(1, 20, .8, [-.8, .5, .25]), new(1, 20, .8, [-.8, .5, .25])]);
    public static WaveformAsset CreateSingleChannelWaveform() => new(Guid.NewGuid(), "One channel", WaveShape.Custom, .2, 20, [1, 1]);
}
