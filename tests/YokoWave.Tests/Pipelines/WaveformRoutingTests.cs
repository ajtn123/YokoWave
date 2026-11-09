using YokoWave.Core;
using YokoWave.Pipelines;
using static YokoWave.Tests.Support.Pipelines.WaveformTestAssets;

namespace YokoWave.Tests.Pipelines;

public sealed class WaveformRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentSourcePortsPreserveChannelValuesAndZeroUndefinedChannelsDuringLiveChanges(bool playlist)
    {
        var multi = CreateThreeChannelWaveform();
        var mono = CreateSingleChannelWaveform();
        var builder = new PipelineBuilder("channels");
        var wave = playlist ? builder.Add(BuiltInNodes.Playlist, new PlaylistNode(multi.Id)) : builder.Add(BuiltInNodes.Waveform, new WaveformNode(multi.Id));
        for (int channel = 0; channel < 3; channel++)
        {
            var sink = builder.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", channel, Limit: 1));
            builder.Connect(wave, WaveformPorts.Level(channel), sink, "strength");
        }

        var runtime = new PipelineRuntime(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(builder.Build(), [multi, mono]), new HashSet<string> { "out" });
        var now = DateTimeOffset.UtcNow;
        var output = runtime.Evaluate(TimeSpan.Zero, now).Outputs["out"];
        Assert.Equal(80, output.ChannelA);
        Assert.Equal(50, output.ChannelB);
        Assert.Equal(25, output.ChannelC);
        Assert.True(runtime.Update(NodeControl.Create(wave.Id, "waveform", mono)));
        var single = runtime.Evaluate(TimeSpan.FromSeconds(.1), now.AddSeconds(.1));
        Assert.Equal(100, single.Outputs["out"].ChannelA);
        Assert.Equal(0, single.Outputs["out"].ChannelB);
        Assert.Equal(0, single.Outputs["out"].ChannelC);
        Assert.Empty(single.Diagnostics);
        Assert.True(runtime.Update(NodeControl.Create(wave.Id, "waveform", multi)));
        var restored = runtime.Evaluate(TimeSpan.FromSeconds(.2), now.AddSeconds(.2)).Outputs["out"];
        Assert.Equal(output.ChannelA, restored.ChannelA);
        Assert.Equal(output.ChannelB, restored.ChannelB);
        Assert.Equal(output.ChannelC, restored.ChannelC);
    }

    [Fact]
    public void ChannelDefinitionsComeFromActualSamplesAndGeneratedWavesHaveOneSourceChannel()
    {
        Assert.Equal(3, CreateThreeChannelWaveform().ChannelCount);
        Assert.Equal(1, CreateSingleChannelWaveform().ChannelCount);
        Assert.Equal(1, new WaveformAsset(Guid.NewGuid(), "Sine", WaveShape.Sine, 1, 20).ChannelCount);
        var variable = CreateThreeChannelWaveform() with
        {
            Samples = [new(1, 20), new(1, 20, Channels: [1, .5])]
        };
        Assert.Equal(2, variable.ChannelCount);
        Assert.Equal(10, DefaultWaveformCatalog.Waveforms.Count(wave => wave.ChannelCount == 3));
    }
}
