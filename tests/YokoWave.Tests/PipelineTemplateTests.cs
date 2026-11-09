using YokoWave.Core;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class PipelineTemplateTests
{
    private static readonly NumericParameterSpec Strength = new("ratio", 0, 1, .3);

    private static double Evaluate(PipelineDocument document)
    {
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(document, []);
        return new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["out"].ChannelA;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedTemplateInputsUseTheOutermostDefaultOrConnection(bool connected)
    {
        var leaf = new PipelineBuilder("leaf");
        var output = leaf.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", Limit: 1));
        var middle = new PipelineBuilder("middle");
        ExposedPort[] innerInputs = [new("strength", "Pipeline.Port.Strength", new(output.Id, "strength"), Strength)];
        var inner = middle.Add(SubgraphExpansion.Type, new SubgraphNode(leaf.Build() with { Inputs = innerInputs }, innerInputs, []));
        var root = new PipelineBuilder("root");
        ExposedPort[] outerInputs = [new("strength", "Pipeline.Port.Strength", new(inner.Id, "strength"), Strength with { DefaultValue = .4 })];
        var outer = root.Add(SubgraphExpansion.Type, new SubgraphNode(middle.Build() with { Inputs = outerInputs }, outerInputs, []));
        if (connected)
        {
            var value = root.Add(BuiltInNodes.Constant, new ConstantNode(.5));
            root.Connect(value, "value", outer, "strength");
        }
        Assert.Equal(connected ? 50 : 40, Evaluate(root.Build()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExposedConnectedInputsKeepTheirInternalRouteUntilOverridden(bool connected)
    {
        var leaf = new PipelineBuilder("leaf");
        var value = leaf.Add(BuiltInNodes.Constant, new ConstantNode(.4));
        var output = leaf.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", Limit: 1));
        leaf.Connect(value, "value", output, "strength");
        var root = new PipelineBuilder("root");
        var wrapper = root.Add(SubgraphExpansion.Type, new SubgraphNode(leaf.Build(),
            [new("strength", "Pipeline.Port.Strength", new(output.Id, "strength"), Strength)], []));
        if (connected)
        {
            var external = root.Add(BuiltInNodes.Constant, new ConstantNode(.5));
            root.Connect(external, "value", wrapper, "strength");
        }
        Assert.Equal(connected ? 50 : 40, Evaluate(root.Build()));
    }

    [Fact]
    public void ExpandingATemplateKeepsItsExposedInputsAndOutputs()
    {
        var leaf = new PipelineBuilder("leaf");
        var value = leaf.Add(BuiltInNodes.Constant, new ConstantNode(.4));
        var output = leaf.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", Limit: 1));
        var root = new PipelineBuilder("root");
        var wrapper = root.Add(SubgraphExpansion.Type, new SubgraphNode(leaf.Build(),
            [new("strength", "Pipeline.Port.Strength", new(output.Id, "strength"), Strength)],
            [new("value", "Pipeline.Port.Value", new(value.Id, "value"))]));
        var document = root.Build() with
        {
            Inputs = [new("strength", "Pipeline.Port.Strength", new(wrapper.Id, "strength"), Strength)],
            Outputs = [new("value", "Pipeline.Port.Value", new(wrapper.Id, "value"))]
        };
        var expanded = SubgraphExpansion.Expand(document);
        var input = Assert.Single(expanded.Inputs!);
        var exposedOutput = Assert.Single(expanded.Outputs!);
        Assert.Contains(expanded.Nodes, n => n.Id == input.Address.Node && n.Type == BuiltInNodes.ChannelOutput);
        Assert.Contains(expanded.Nodes, n => n.Id == exposedOutput.Address.Node && n.Type == BuiltInNodes.Constant);
        Assert.Equal(30, Evaluate(expanded));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("direction")]
    [InlineData("kind")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("default")]
    public void InvalidUnusedTemplatePortsAreRejectedWithoutCrashing(string invalid)
    {
        var leaf = new PipelineBuilder("leaf");
        var output = leaf.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out"));
        var port = new ExposedPort("strength", "Pipeline.Port.Strength", new(output.Id, "strength"), Strength);
        ExposedPort[] inputs = invalid switch
        {
            "missing" => [port with { Address = new(output.Id, "missing") }],
            "direction" => [port with { Address = new(output.Id, "strength") }],
            "kind" => [port with { Kind = PortValueKind.Intent }],
            "duplicate" => [port, port],
            "null" => [null!],
            "default" => [port with { Parameter = Strength with { Maximum = 2, DefaultValue = 2 } }],
            _ => throw new InvalidOperationException()
        };
        var root = new PipelineBuilder("root");
        root.Add(SubgraphExpansion.Type, new SubgraphNode(leaf.Build(), invalid == "direction" ? [] : inputs, invalid == "direction" ? inputs : []));
        Assert.NotEmpty(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Validate(root.Build(), []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrSimulatedExternalInputsDoNotFallBackToTheInternalRoute(bool simulated)
    {
        var leaf = new PipelineBuilder("leaf");
        var value = leaf.Add(BuiltInNodes.Constant, new ConstantNode(.4));
        var output = leaf.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out"));
        leaf.Connect(value, "value", output, "strength");
        var root = new PipelineBuilder("root");
        var wrapper = root.Add(SubgraphExpansion.Type, new SubgraphNode(leaf.Build(),
            [new("strength", "Pipeline.Port.Strength", new(output.Id, "strength"), Strength)], []));
        var input = simulated ? root.Add(BuiltInNodes.SimulatedInput, new SimulatedInputNode(.5)) : root.Add(BuiltInNodes.Sensor, new SensorNode("sensor"));
        root.Connect(input, simulated ? "value" : "pressure", wrapper, "strength");
        Assert.Equal(0, Evaluate(root.Build()));
    }

    [Theory]
    [InlineData(PlaybackOrder.Repeat)]
    [InlineData(PlaybackOrder.Once)]
    [InlineData(PlaybackOrder.Sequential)]
    [InlineData(PlaybackOrder.Random)]
    public void DuplicatePlaylistEntriesAreRejectedBeforePlayback(PlaybackOrder order)
    {
        var waveform = WaveformPreset.BuiltIn[0];
        var graph = new PipelineBuilder("playlist");
        var playlist = graph.Add(BuiltInNodes.Playlist, new PlaylistNode(waveform.Id, [waveform.Id, waveform.Id], order));
        var errors = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Validate(graph.Build(), [waveform]);
        Assert.Contains(errors, e => e.Node == playlist.Id && e.Key == "Pipeline.Error.Configuration");
    }
}
