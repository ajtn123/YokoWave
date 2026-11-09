using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.Tests.Support.Pipelines;

internal static class PipelineTestGraphs
{
    public static DeviceDescriptor Device { get; } = new("one", "one", DeviceKind.DualChannel, 80, -40);
    public static DateTimeOffset EvaluationTime { get; } = DateTimeOffset.Parse("2026-11-08T00:00:00Z");

    public static PipelineDocument CreateGraph(NodeDefinition[] nodes, params PipelineConnection[] edges) => new(Guid.NewGuid(), "test", nodes, edges, []);
    public static NodeDefinition CreateNumber(double value) => NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(value));
    public static NodeDefinition CreateOutput(int channel = 0) => NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode("one", channel, Limit: 1));
    public static PipelineConnection CreateEdge(NodeDefinition from, NodeDefinition to, string target = "strength", string source = "value") => new(new(from.Id, source), new(to.Id, target));
    public static PipelineCompiler CreateCompiler() => new(BuiltInNodes.CreateRegistry());
    public static PipelinePlan CreatePlan(double strength = .5, double? duration = null)
    {
        var value = CreateNumber(strength);
        var output = CreateOutput();
        return CreateCompiler().Compile(CreateGraph([value, output], CreateEdge(value, output)) with { DurationSeconds = duration ?? 90, TimerEnabled = duration.HasValue }, []);
    }

    public static WaveformAsset CreateWaveform(double frequency = 20, double seconds = 1) => new(Guid.NewGuid(), "wave", WaveShape.Custom, seconds, frequency, [1, 1]);
    public static PipelineRuntime CreateRuntime(PipelineBuilder graph, params WaveformAsset[] waves) => new(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph.Build(), waves), new HashSet<string> { "out", "sensor" });
    public static NodeDefinition AddOutput(PipelineBuilder b, NodeDefinition source, string port = "value", int channel = 0, double limit = .6)
    {
        var output = b.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", channel, Limit: limit));
        b.Connect(source, port, output, "strength");
        return output;
    }

    public static double ReadLevel(PipelineRuntime runtime, double seconds = 0) => runtime.Evaluate(TimeSpan.FromSeconds(seconds), EvaluationTime.AddSeconds(seconds)).Outputs["out"].ChannelA;
}
