using System.Text.Json;
using YokoWave.Core;
using YokoWave.Pipelines;
using static YokoWave.Tests.Support.Pipelines.PipelineTestGraphs;

namespace YokoWave.Tests.Pipelines;

public sealed class TemplateExecutionTests
{
    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryTemplateIsAnOrdinaryGraphWithFreshIndependentState(string key)
    {
        var graph = PipelineTemplates.Create(key, new("template", "out", "sensor", WaveformAsset.BuiltIn[0]));
        Assert.NotEmpty(graph.Nodes);
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, WaveformAsset.BuiltIn);
        var first = new PipelineRuntime(plan, new HashSet<string> { "out", "sensor" });
        var next = new PipelineRuntime(plan, new HashSet<string> { "out", "sensor" });
        string initial = JsonSerializer.Serialize(first.Evaluate(TimeSpan.Zero, EvaluationTime));
        foreach (var node in graph.Nodes.Where(n => n.Type == BuiltInNodes.Constant))
            first.SetConstant(node.Id, .8);
        first.Evaluate(TimeSpan.FromSeconds(1), EvaluationTime.AddSeconds(1));
        Assert.Equal(initial, JsonSerializer.Serialize(next.Evaluate(TimeSpan.Zero, EvaluationTime)));
        Assert.All(first.Evaluate(TimeSpan.Zero, EvaluationTime).Outputs.Values, f => Assert.InRange(f.ChannelA, 0, 60));
    }

    [Fact]
    public void SubgraphInstancesHaveIndependentIdsResourcesPortsAndDeviceMappings()
    {
        var inner = new PipelineBuilder("inner", 1);
        var value = inner.Add(BuiltInNodes.Constant, new ConstantNode(.4));
        AddOutput(inner, value);
        var graph = inner.Build();
        var b = new PipelineBuilder("outer", 1);
        var first = b.Add(SubgraphExpansion.Type, new SubgraphNode(graph, [], [new("value", "Pipeline.Port.Value", new(value.Id, "value"))], new() { ["out"] = "one" }));
        b.Add(SubgraphExpansion.Type, new SubgraphNode(graph, [], [], new() { ["out"] = "two" }));
        var expanded = SubgraphExpansion.Expand(b.Build());
        Assert.Equal(4, expanded.Nodes.Select(n => n.Id).Distinct().Count());
        var runtime = CreateRuntime(b);
        var outputs = runtime.Evaluate(TimeSpan.Zero, EvaluationTime).Outputs;
        Assert.Equal(40, outputs["one"].ChannelA);
        Assert.Equal(40, outputs["two"].ChannelA);
        var rebound = SubgraphExpansion.MapDevices(first, new Dictionary<string, string> { ["one"] = "three" });
        Assert.Equal("three", rebound.Configuration.GetProperty("Devices").GetProperty("out").GetString());
    }

    [Fact]
    public void WaveformTemplatePreservesIndependentAssetChannels()
    {
        var waveform = new WaveformAsset(Guid.NewGuid(), "channels", WaveShape.Sampled, 1, 20, Samples: [new(1, 20, 1, [-1, .5]), new(1, 20, 1, [-1, .5])]);
        var graph = PipelineTemplates.Create("waveform", new("channels", "out", Waveform: waveform, Limit: 1));
        var runtime = new PipelineRuntime(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, [waveform]), new HashSet<string>());
        foreach (var node in graph.Nodes.Where(n => n.Type == BuiltInNodes.Constant))
            runtime.SetConstant(node.Id, .4);
        var frame = runtime.Evaluate(TimeSpan.Zero, EvaluationTime).Outputs["out"];
        Assert.Equal(40, frame.ChannelA);
        Assert.Equal(20, frame.ChannelB);
    }

    [Fact]
    public void ExposedSubgraphDefaultsApplyAndCanBeOverriddenByTheOuterGraph()
    {
        var inner = new PipelineBuilder("inner", 1);
        var output = inner.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out"));
        var b = new PipelineBuilder("outer", 1);
        var sub = b.Add(SubgraphExpansion.Type, new SubgraphNode(inner.Build(), [new("strength", "Pipeline.Port.Strength", new(output.Id, "strength"), new("ratio", 0, 1, .3))], []));
        Assert.Equal(30, ReadLevel(CreateRuntime(b)));
        var value = b.Add(BuiltInNodes.Constant, new ConstantNode(.4));
        b.Connect(value, "value", sub, "strength");
        Assert.Equal(40, ReadLevel(CreateRuntime(b)));
    }

    public static IEnumerable<object[]> Templates => PipelineTemplates.All.Select(t => new object[] { t.Key });
}
