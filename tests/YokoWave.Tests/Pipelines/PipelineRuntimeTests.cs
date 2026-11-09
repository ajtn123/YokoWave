using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using static YokoWave.Tests.Support.Pipelines.PipelineTestGraphs;

namespace YokoWave.Tests.Pipelines;

public sealed class PipelineRuntimeTests
{
    [Fact]
    public void ChannelCapsAreAppliedIndependentlyAndFramesMergeOncePerDevice()
    {
        var strength = CreateNumber(.9);
        var capA = CreateNumber(.8);
        var capB = CreateNumber(.4);
        var a = CreateOutput();
        var b = CreateOutput(1);
        var graph = CreateGraph([strength, capA, capB, a, b], CreateEdge(strength, a), CreateEdge(strength, b), CreateEdge(capA, a, "limit"), CreateEdge(capB, b, "limit"));
        var frame = Assert.Single(new PipelineRuntime(CreateCompiler().Compile(graph, []), new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs).Value;
        Assert.Equal(80, frame.ChannelA);
        Assert.Equal(40, frame.ChannelB);
        Assert.Equal(1, frame.ReferenceSample!.Level);
        Assert.Equal(4, frame.WindowA!.Count);
        var capped = new PipelineRuntime(CreateCompiler().Compile(graph with { Nodes = graph.Nodes.Select(n => n.Id == a.Id ? NodeDefinition.Create(n.Type, new ChannelOutputNode("one", Limit: .7), n.Id) : n).ToArray() }, []), new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"];
        Assert.Equal(70, capped.ChannelA);
        Assert.Equal(40, capped.ChannelB);
    }

    [Fact]
    public void LiveLimitsBelongToOutputNodesAndNeverClampOtherChannelsOrDevices()
    {
        var level = CreateNumber(.9);
        var a = CreateOutput();
        var b = CreateOutput(1);
        var other = NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode("other", Limit: .8));
        var runtime = new PipelineRuntime(CreateCompiler().Compile(CreateGraph([level, a, b, other], CreateEdge(level, a), CreateEdge(level, b), CreateEdge(level, other)), []), new HashSet<string>());
        runtime.SetLimit(a.Id, .2);
        var frames = runtime.Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs;
        Assert.Equal(20, frames["one"].ChannelA);
        Assert.Equal(90, frames["one"].ChannelB);
        Assert.Equal(80, frames["other"].ChannelA);
        runtime.SetLimit(b.Id, .4);
        frames = runtime.Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs;
        Assert.Equal(20, frames["one"].ChannelA);
        Assert.Equal(40, frames["one"].ChannelB);
        Assert.Equal(80, frames["other"].ChannelA);
    }

    [Fact]
    public void ThirdMotorChannelUsesItsOwnSamplesInsteadOfChannelA()
    {
        var zero = CreateNumber(0);
        var strength = CreateNumber(.5);
        var a = CreateOutput();
        var c = CreateOutput(2);
        var plan = CreateCompiler().Compile(CreateGraph([zero, strength, a, c], CreateEdge(zero, a), CreateEdge(strength, c)), []);
        var frame = new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"];
        Assert.Equal(0, WaveformRendering.Strength(frame, 0));
        Assert.Equal(50, WaveformRendering.Strength(frame, 2));
        byte[] packet = YokonexToyProtocol.MotorOutput(frame, 7);
        Assert.Equal(0, packet[2]);
        Assert.True(packet[4] > 0);
    }

    [Fact]
    public void WaveformSamplesRetainFrequencyWidthAnd25MillisecondResolution()
    {
        var waveform = new WaveformAsset(Guid.NewGuid(), "sampled", WaveShape.Sampled, .2, 20, Samples: [new(.1, 10, .2), new(.9, 80, .7)]);
        var wave = NodeDefinition.Create(BuiltInNodes.Waveform, new WaveformNode(waveform.Id));
        var cap = CreateNumber(1);
        var output = CreateOutput();
        var plan = CreateCompiler().Compile(CreateGraph([wave, cap, output], CreateEdge(wave, output, source: "levelA"), CreateEdge(wave, output, "frequency", "frequency"), CreateEdge(wave, output, "pulseWidth", "pulseWidth"), CreateEdge(cap, output, "limit")), [waveform]);
        waveform.Samples![0] = new(1, 100); // Editing the asset after compilation cannot change a running snapshot.
        var frame = new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.FromSeconds(.075), DateTimeOffset.UtcNow).Outputs["one"];
        Assert.Equal(90, frame.ChannelA);
        Assert.Equal(10, frame.WindowA![0].FrequencyHz);
        Assert.Equal(.2, frame.WindowA[0].PulseWidth);
        Assert.Equal(80, frame.WindowA[1].FrequencyHz);
        Assert.Equal(.7, frame.WindowA[1].PulseWidth);
        Assert.Equal(.1 / .9, frame.WindowA[0].Level, 8);
    }

    [Fact]
    public void StrengthProgressionPreservesBoostSamplesExpiryAndSimulatedProvenance()
    {
        var now = DateTimeOffset.UtcNow;
        NumericSignal boost = new([0, 1, 0, 1], now.AddSeconds(1));
        var registry = BuiltInNodes.CreateRegistry().Register(new NodeRegistration<TestInputNode>("test.boost", "Pipeline.Port.boost", [NodePort.Output("value", "Pipeline.Port.Value")], _ => true, (_, _) => new TestInputProcessor(() => boost)));
        var input = NodeDefinition.Create("test.boost", new TestInputNode());
        var level = CreateNumber(.4);
        var automation = NodeDefinition.Create(BuiltInNodes.Automation, new AutomationNode(new(Boost: 20)));
        var output = CreateOutput();
        var plan = new PipelineCompiler(registry).Compile(CreateGraph([input, level, automation, output], CreateEdge(level, automation, "value"), CreateEdge(input, automation, "boost"), CreateEdge(automation, output)), []);
        var runtime = new PipelineRuntime(plan, new HashSet<string>());
        var frame = runtime.Evaluate(TimeSpan.Zero, now).Outputs["one"];
        double[] expected = new[]
        {
            .4,
            .6,
            .4,
            .6
        };
        Assert.Equal(expected.Length, frame.WindowA!.Count);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], frame.WindowA[i].Level * frame.ChannelA / 100, 8);
        Assert.Equal(0, runtime.Evaluate(TimeSpan.Zero, now.AddSeconds(2)).Outputs["one"].ChannelA);
        boost = NumericSignal.Missing;
        Assert.Equal(0, runtime.Evaluate(TimeSpan.Zero, now).Outputs["one"].ChannelA);
        boost = new([1], now.AddSeconds(1), Simulated: true);
        Assert.Equal(0, runtime.Evaluate(TimeSpan.Zero, now).Outputs["one"].ChannelA);
        Assert.Equal(60, new PipelineRuntime(plan, new HashSet<string> { "one" }).Evaluate(TimeSpan.Zero, now).Outputs["one"].ChannelA, 8);
    }

    [Fact]
    public void MissingExpiredInvalidAndSimulatedInputsCannotReachRealOutputsAndFanoutEvaluatesOnce()
    {
        var now = DateTimeOffset.UtcNow;
        NumericSignal signal = new([.5], now.AddSeconds(1));
        var processor = new TestInputProcessor(() => signal);
        var registry = BuiltInNodes.CreateRegistry().Register(new NodeRegistration<TestInputNode>("test.input", "Pipeline.Port.Value", [NodePort.Output("value", "Pipeline.Port.Value")], _ => true, (_, _) => processor));
        var input = NodeDefinition.Create("test.input", new TestInputNode());
        var gain = NodeDefinition.Create(BuiltInNodes.Gain, new GainNode());
        var a = CreateOutput();
        var b = CreateOutput(1);
        var plan = new PipelineCompiler(registry).Compile(CreateGraph([input, gain, a, b], CreateEdge(input, gain, "value"), CreateEdge(gain, a), CreateEdge(gain, b)), []);
        var runtime = new PipelineRuntime(plan, new HashSet<string>());
        Assert.Equal(50, runtime.Evaluate(TimeSpan.Zero, now).Outputs["one"].ChannelA);
        Assert.Equal(1, processor.Evaluations);
        Assert.Equal(0, runtime.Evaluate(TimeSpan.Zero, now.AddSeconds(2)).Outputs["one"].ChannelA);
        signal = NumericSignal.Missing;
        Assert.Equal(0, runtime.Evaluate(TimeSpan.Zero, now).Outputs["one"].ChannelA);
        signal = new([double.NaN], now.AddSeconds(1));
        Assert.Equal(0, runtime.Evaluate(TimeSpan.Zero, now).Outputs["one"].ChannelA);
        signal = new([.5], now.AddSeconds(1), Simulated: true);
        Assert.Equal(0, runtime.Evaluate(TimeSpan.Zero, now).Outputs["one"].ChannelA);
        Assert.Equal(50, new PipelineRuntime(plan, new HashSet<string> { "one" }).Evaluate(TimeSpan.Zero, now).Outputs["one"].ChannelA);
        signal = new([.3], now.AddSeconds(3));
        Assert.Equal(30, runtime.Evaluate(TimeSpan.Zero, now.AddSeconds(2)).Outputs["one"].ChannelA);
    }

    [Fact]
    public void LiveValuesApplyOnNextFrameAndNewRunsDoNotReuseOverrides()
    {
        var number = CreateNumber(.3);
        var output = CreateOutput();
        var plan = CreateCompiler().Compile(CreateGraph([number, output], CreateEdge(number, output)), []);
        var runtime = new PipelineRuntime(plan, new HashSet<string>());
        Assert.True(runtime.SetConstant(number.Id, .5));
        Assert.False(runtime.SetConstant(number.Id, double.NaN));
        Assert.Equal(50, runtime.Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"].ChannelA);
        Assert.Equal(30, new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"].ChannelA);
        Assert.True(runtime.SetConstant(number.Id, 2)); // Dynamic values are still guarded at the target input.
        Assert.Equal(0, runtime.Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"].ChannelA);
    }

    [Fact]
    public void SampleWindowIsZeroBeyondSessionDeadline()
    {
        var runtime = new PipelineRuntime(CreatePlan(.5, 1), new HashSet<string>());
        var frame = runtime.Evaluate(TimeSpan.FromSeconds(.975), DateTimeOffset.UtcNow).Outputs["one"];
        Assert.Equal(1, frame.WindowA![0].Level);
        Assert.All(frame.WindowA.Skip(1), s => Assert.Equal(0, s.Level));
    }

    [Fact]
    public void PumpUsesNumericDiscreteInputsAndNoInventedStrength()
    {
        var direction = CreateNumber(2);
        var water = CreateNumber(1);
        var seconds = CreateNumber(17);
        var pump = NodeDefinition.Create(BuiltInNodes.PumpOutput, new PumpOutputNode("pump"));
        var plan = CreateCompiler().Compile(CreateGraph([direction, water, seconds, pump], CreateEdge(direction, pump, "direction"), CreateEdge(water, pump, "water"), CreateEdge(seconds, pump, "peristalticSeconds"), CreateEdge(seconds, pump, "waterSeconds")), []);
        var frame = new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["pump"];
        Assert.Equal(new PumpOutput(PumpDirection.Reverse, true, 17, 17), frame.Pump);
        Assert.Equal(0, frame.ChannelA);
    }

    [Theory]
    [InlineData(BuiltInNodes.Gain)]
    [InlineData(BuiltInNodes.Limit)]
    [InlineData(BuiltInNodes.Smooth)]
    [InlineData(BuiltInNodes.Threshold)]
    public void NumericProcessingNeverRemovesSimulatedProvenance(string type)
    {
        var b = new PipelineBuilder("simulated", 1);
        var input = b.Add(BuiltInNodes.SimulatedInput, new SimulatedInputNode(.5));
        var processor = type switch
        {
            BuiltInNodes.Gain => b.Add(type, new GainNode()),
            BuiltInNodes.Limit => b.Add(type, new LimitNode()),
            BuiltInNodes.Smooth => b.Add(type, new SmoothNode()),
            _ => b.Add(type, new ThresholdNode(.4))
        };
        b.Connect(input, "value", processor, "value");
        AddOutput(b, processor);
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(b.Build(), []);
        Assert.Equal(0, ReadLevel(new(plan, new HashSet<string>())));
        Assert.True(ReadLevel(new(plan, new HashSet<string> { "out" })) > 0);
    }

    private sealed record TestInputNode;
    private sealed class TestInputProcessor(Func<NumericSignal> read) : ISignalProcessor
    {
        public int Evaluations { get; private set; }

        public void Evaluate(NodeEvaluationContext context)
        {
            Evaluations++;
            context.Output("value", read());
        }
    }
}
