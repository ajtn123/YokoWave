using YokoWave.Core;
using YokoWave.Pipelines;
using static YokoWave.Tests.Support.Pipelines.PipelineTestGraphs;

namespace YokoWave.Tests.Pipelines;

public sealed class PipelineCompilerTests
{
    [Fact]
    public void CompilerRejectsCyclesMultipleSourcesUnknownTypesAndWrongPorts()
    {
        var number = CreateNumber(.3);
        var gain = NodeDefinition.Create(BuiltInNodes.Gain, new GainNode());
        var other = NodeDefinition.Create(BuiltInNodes.Gain, new GainNode());
        var output = CreateOutput();
        var graph = CreateGraph([number, gain, other, output], CreateEdge(gain, other, "value"), CreateEdge(other, gain, "value"), CreateEdge(gain, output), CreateEdge(number, output), new(new(number.Id, "unknown"), new(gain.Id, "gain")));
        var errors = CreateCompiler().Validate(graph, []);
        Assert.Contains(errors, d => d.Key == "Pipeline.Error.Cycle");
        Assert.Contains(errors, d => d.Key == "Pipeline.Error.MultipleSources");
        Assert.Contains(errors, d => d.Key == "Pipeline.Error.Connection");
        Assert.Contains(CreateCompiler().Validate(graph with { Nodes = [number with { Type = "unknown" }, output] }, []), d => d.Key == "Pipeline.Error.UnknownNode");
        Assert.Throws<PipelineValidationException>(() => CreateCompiler().Compile(graph, []));
    }

    [Fact]
    public void CompilerRejectsDuplicateWritersAndUnsupportedChannels()
    {
        var a = CreateOutput();
        var b = CreateOutput();
        Assert.Contains(CreateCompiler().Validate(CreateGraph([a, b]), []), d => d.Key == "Pipeline.Error.DuplicateOutput");
        var devices = new Dictionary<string, DeviceDescriptor>
        {
            ["one"] = Device with
            {
                ChannelMask = 2
            }
        };
        Assert.Contains(CreateCompiler().Validate(CreateGraph([a]), [], devices), d => d.Key == "Pipeline.Error.Device");
        Assert.Empty(CreateCompiler().Validate(CreateGraph([CreateOutput(1)]), [], devices));
    }

    [Fact]
    public void ScalarPortsShareATypeAndRespectTargetRangesAndDiscreteValues()
    {
        var number = CreateNumber(20);
        var output = CreateOutput();
        var plan = CreateCompiler().Compile(CreateGraph([number, output], CreateEdge(number, output, "frequency")), []);
        Assert.Equal(20, new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"].Frequency);
        Assert.Contains(CreateCompiler().Validate(CreateGraph([number, output], CreateEdge(number, output)), []), d => d.Key == "Pipeline.Error.Range");
        var pump = NodeDefinition.Create(BuiltInNodes.PumpOutput, new PumpOutputNode("pump"));
        var fraction = CreateNumber(.5);
        Assert.Contains(CreateCompiler().Validate(CreateGraph([fraction, pump], CreateEdge(fraction, pump, "direction")), []), d => d.Key == "Pipeline.Error.Range");
    }
}
