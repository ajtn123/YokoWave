using System.Collections.Concurrent;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class PipelineTests
{
    private static readonly DeviceDescriptor Device = new("one", "one", DeviceKind.DualChannel, 80, -40);
    private static PipelineDocument Graph(NodeDefinition[] nodes, params PipelineConnection[] edges) =>
        new(Guid.NewGuid(), "test", nodes, edges, []);
    private static NodeDefinition Number(double value) => NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(value));
    private static NodeDefinition Output(int channel = 0) => NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode("one", channel, Limit: 1));
    private static PipelineConnection Edge(NodeDefinition from, NodeDefinition to, string target = "strength", string source = "value") => new(new(from.Id, source), new(to.Id, target));
    private static PipelineCompiler Compiler() => new(BuiltInNodes.CreateRegistry());
    private static PipelinePlan Plan(double strength = .5, double? duration = null)
    {
        var value = Number(strength);
        var output = Output();
        return Compiler().Compile(Graph([value, output], Edge(value, output)) with
        {
            DurationSeconds = duration ?? 90,
            TimerEnabled = duration.HasValue
        }, []);
    }

    [Fact]
    public void CompilerRejectsCyclesMultipleSourcesUnknownTypesAndWrongPorts()
    {
        var number = Number(.3);
        var gain = NodeDefinition.Create(BuiltInNodes.Gain, new GainNode());
        var other = NodeDefinition.Create(BuiltInNodes.Gain, new GainNode());
        var output = Output();
        var graph = Graph([number, gain, other, output], Edge(gain, other, "value"), Edge(other, gain, "value"),
            Edge(gain, output), Edge(number, output), new(new(number.Id, "unknown"), new(gain.Id, "gain")));
        var errors = Compiler().Validate(graph, []);
        Assert.Contains(errors, d => d.Key == "Pipeline.Error.Cycle");
        Assert.Contains(errors, d => d.Key == "Pipeline.Error.MultipleSources");
        Assert.Contains(errors, d => d.Key == "Pipeline.Error.Connection");
        Assert.Contains(Compiler().Validate(graph with
        {
            Nodes = [number with { Type = "unknown" }, output]
        }, []), d => d.Key == "Pipeline.Error.UnknownNode");
        Assert.Throws<PipelineValidationException>(() => Compiler().Compile(graph, []));
    }

    [Fact]
    public void CompilerRejectsDuplicateWritersAndUnsupportedChannels()
    {
        var a = Output();
        var b = Output();
        Assert.Contains(Compiler().Validate(Graph([a, b]), []), d => d.Key == "Pipeline.Error.DuplicateOutput");
        var devices = new Dictionary<string, DeviceDescriptor> { ["one"] = Device with { ChannelMask = 2 } };
        Assert.Contains(Compiler().Validate(Graph([a]), [], devices), d => d.Key == "Pipeline.Error.Device");
        Assert.Empty(Compiler().Validate(Graph([Output(1)]), [], devices));
    }

    [Fact]
    public void ScalarPortsShareATypeAndRespectTargetRangesAndDiscreteValues()
    {
        var number = Number(20);
        var output = Output();
        var plan = Compiler().Compile(Graph([number, output], Edge(number, output, "frequency")), []);
        Assert.Equal(20, new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"].Frequency);
        Assert.Contains(Compiler().Validate(Graph([number, output], Edge(number, output)), []), d => d.Key == "Pipeline.Error.Range");
        var pump = NodeDefinition.Create(BuiltInNodes.PumpOutput, new PumpOutputNode("pump"));
        var fraction = Number(.5);
        Assert.Contains(Compiler().Validate(Graph([fraction, pump], Edge(fraction, pump, "direction")), []), d => d.Key == "Pipeline.Error.Range");
    }

    [Fact]
    public void ChannelCapsAreAppliedIndependentlyAndFramesMergeOncePerDevice()
    {
        var strength = Number(.9);
        var capA = Number(.8);
        var capB = Number(.4);
        var a = Output();
        var b = Output(1);
        var graph = Graph([strength, capA, capB, a, b], Edge(strength, a), Edge(strength, b), Edge(capA, a, "limit"), Edge(capB, b, "limit"));
        var frame = Assert.Single(new PipelineRuntime(Compiler().Compile(graph, []), new HashSet<string>())
            .Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs).Value;
        Assert.Equal(80, frame.ChannelA);
        Assert.Equal(40, frame.ChannelB);
        Assert.Equal(1, frame.ReferenceSample!.Level);
        Assert.Equal(4, frame.WindowA!.Count);
        var capped = new PipelineRuntime(Compiler().Compile(graph with
        {
            Nodes = graph.Nodes.Select(n => n.Id == a.Id ? NodeDefinition.Create(n.Type, new ChannelOutputNode("one", Limit: .7), n.Id) : n).ToArray()
        }, []), new HashSet<string>())
            .Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"];
        Assert.Equal(70, capped.ChannelA);
        Assert.Equal(40, capped.ChannelB);
    }

    [Fact]
    public void LiveLimitsBelongToOutputNodesAndNeverClampOtherChannelsOrDevices()
    {
        var level = Number(.9);
        var a = Output();
        var b = Output(1);
        var other = NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode("other", Limit: .8));
        var runtime = new PipelineRuntime(Compiler().Compile(Graph([level, a, b, other], Edge(level, a), Edge(level, b), Edge(level, other)), []), new HashSet<string>());
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
        var zero = Number(0);
        var strength = Number(.5);
        var a = Output();
        var c = Output(2);
        var plan = Compiler().Compile(Graph([zero, strength, a, c], Edge(zero, a), Edge(strength, c)), []);
        var frame = new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["one"];
        Assert.Equal(0, WaveformRendering.Strength(frame, 0));
        Assert.Equal(50, WaveformRendering.Strength(frame, 2));
        var packet = YokonexToyProtocol.MotorOutput(frame, 7);
        Assert.Equal(0, packet[2]);
        Assert.True(packet[4] > 0);
    }

    [Fact]
    public void WaveformSamplesRetainFrequencyWidthAnd25MillisecondResolution()
    {
        var preset = new WaveformPreset(Guid.NewGuid(), "sampled", WaveShape.Sampled, .2, 20,
            Samples: [new(.1, 10, .2), new(.9, 80, .7)]);
        var wave = NodeDefinition.Create(BuiltInNodes.Waveform, new WaveformNode(preset.Id));
        var cap = Number(1);
        var output = Output();
        var plan = Compiler().Compile(Graph([wave, cap, output], Edge(wave, output, source: "levelA"),
            Edge(wave, output, "frequency", "frequency"), Edge(wave, output, "pulseWidth", "pulseWidth"), Edge(cap, output, "limit")), [preset]);
        preset.Samples![0] = new(1, 100); // Editing the asset after compilation cannot change a running snapshot.
        var frame = new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.FromSeconds(.075), DateTimeOffset.UtcNow).Outputs["one"];
        Assert.Equal(90, frame.ChannelA);
        Assert.Equal(10, frame.WindowA![0].FrequencyHz);
        Assert.Equal(.2, frame.WindowA[0].PulseWidth);
        Assert.Equal(80, frame.WindowA[1].FrequencyHz);
        Assert.Equal(.7, frame.WindowA[1].PulseWidth);
        Assert.Equal(.1 / .9, frame.WindowA[0].Level, 8);
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

    [Fact]
    public void MissingExpiredInvalidAndSimulatedInputsCannotReachRealOutputsAndFanoutEvaluatesOnce()
    {
        var now = DateTimeOffset.UtcNow;
        NumericSignal signal = new([.5], now.AddSeconds(1));
        var processor = new TestInputProcessor(() => signal);
        var registry = BuiltInNodes.CreateRegistry().Register(new NodeRegistration<TestInputNode>("test.input", "Pipeline.Port.Value",
            [NodePort.Output("value", "Pipeline.Port.Value")], _ => true, (_, _) => processor));
        var input = NodeDefinition.Create("test.input", new TestInputNode());
        var gain = NodeDefinition.Create(BuiltInNodes.Gain, new GainNode());
        var a = Output();
        var b = Output(1);
        var plan = new PipelineCompiler(registry).Compile(Graph([input, gain, a, b], Edge(input, gain, "value"), Edge(gain, a), Edge(gain, b)), []);
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
        var number = Number(.3);
        var output = Output();
        var plan = Compiler().Compile(Graph([number, output], Edge(number, output)), []);
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
        var runtime = new PipelineRuntime(Plan(.5, 1), new HashSet<string>());
        var frame = runtime.Evaluate(TimeSpan.FromSeconds(.975), DateTimeOffset.UtcNow).Outputs["one"];
        Assert.Equal(1, frame.WindowA![0].Level);
        Assert.All(frame.WindowA.Skip(1), s => Assert.Equal(0, s.Level));
    }

    [Fact]
    public void PumpUsesNumericDiscreteInputsAndNoInventedStrength()
    {
        var direction = Number(2);
        var water = Number(1);
        var seconds = Number(17);
        var pump = NodeDefinition.Create(BuiltInNodes.PumpOutput, new PumpOutputNode("pump"));
        var plan = Compiler().Compile(Graph([direction, water, seconds, pump], Edge(direction, pump, "direction"),
            Edge(water, pump, "water"), Edge(seconds, pump, "peristalticSeconds"), Edge(seconds, pump, "waterSeconds")), []);
        var frame = new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs["pump"];
        Assert.Equal(new PumpOutput(PumpDirection.Reverse, true, 17, 17), frame.Pump);
        Assert.Equal(0, frame.ChannelA);
    }

    [Fact]
    public async Task SessionRunsWithoutUiPausesWithOwnershipAndStopsBeforeRelease()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(Plan());
        await Wait(() => transport.Last.ChannelA == 50);
        Assert.Equal(session.OwnerId, controller.DeviceOwner("one"));
        await session.PauseAsync();
        Assert.Equal(PipelineState.Paused, session.State);
        Assert.Equal(0, transport.Last.ChannelA);
        Assert.Equal(session.OwnerId, controller.DeviceOwner("one"));
        await using var other = new PipelineSession(controller);
        await Assert.ThrowsAsync<LocalizedOperationException>(() => other.StartAsync(Plan()));
        await session.PauseAsync();
        await Wait(() => transport.Last.ChannelA == 50);
        SessionRecord? record = null;
        session.Completed += (_, item) => record = item;
        await session.StopAsync();
        Assert.Equal(0, transport.Last.ChannelA);
        Assert.Null(controller.DeviceOwner("one"));
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.NotNull(record);
        Assert.Equal(50, record.PeakA);
    }

    [Fact]
    public async Task ZeroStrengthStartsAndTimerStopsWithoutUiTicks()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(Plan(0, 1));
        Assert.Equal(PipelineState.Running, session.State);
        await Wait(() => session.State == PipelineState.Idle);
        Assert.Null(controller.DeviceOwner("one"));
        Assert.All(transport.Frames, f => Assert.Equal(0, f.ChannelA));
    }

    [Fact]
    public async Task ObserverFailureCannotInterruptOutputOrMarkConfirmedStopAsFaulted()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        session.Changed += (_, _) => throw new InvalidOperationException("observer");
        session.Completed += (_, _) => throw new InvalidOperationException("observer");
        await session.StartAsync(Plan());
        await Wait(() => transport.Last.ChannelA == 50);
        await session.StopAsync();
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.Null(controller.DeviceOwner("one"));
    }

    [Fact]
    public async Task IndependentDevicesDispatchTogetherAndStopWaitsForInFlightWrite()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await controller.ConnectAsync(Device with
        {
            Id = "fast"
        }, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        var number = Number(.5);
        var first = Output();
        var second = NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode("fast"));
        var plan = Compiler().Compile(Graph([number, first, second], Edge(number, first), Edge(number, second)), []);
        transport.BlockNonzero = true;
        var start = session.StartAsync(plan);
        Task? stop = null;
        try
        {
            await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await transport.FastWrite.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            stop = session.StopAsync();
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            transport.ReleaseWrite.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            await (stop ?? session.StopAsync());
        }
        Assert.Null(controller.DeviceOwner("one"));
        Assert.Null(controller.DeviceOwner("fast"));
        Assert.Equal(0, transport.Last.ChannelA);
    }

    [Fact]
    public async Task StopFailureKeepsDeviceReservedUntilRetrySucceeds()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(Plan());
        await Wait(() => transport.Last.ChannelA > 0);
        transport.FailZero = true;
        await Assert.ThrowsAnyAsync<Exception>(() => session.StopAsync());
        Assert.Equal(PipelineState.Faulted, session.State);
        Assert.Equal(session.OwnerId, controller.DeviceOwner("one"));
        transport.FailZero = false;
        await session.StopAsync();
        Assert.Null(controller.DeviceOwner("one"));
        Assert.Equal(0, transport.Last.ChannelA);
    }

    [Fact]
    public async Task OutputFailureAndFailedCleanupPreserveBothErrors()
    {
        var transport = new PipelineTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(Device, TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller);
        await session.StartAsync(Plan());
        transport.FailNonzero = true;
        transport.FailZero = true;
        await Wait(() => session.State == PipelineState.Faulted);
        var errors = Assert.IsAssignableFrom<AggregateException>(session.Error).Flatten().InnerExceptions;
        Assert.Contains(errors, e => e.Message == "write failed");
        Assert.Contains(errors, e => e.Message == "zero failed");
        Assert.Equal(session.OwnerId, controller.DeviceOwner("one"));
        transport.FailZero = false;
        await session.StopAsync();
    }

    [Fact]
    public async Task DocumentsRoundTripWithoutRuntimeStateAndCorruptionCannotBeOverwritten()
    {
        using var directory = new TestFolder();
        var store = new PipelineStore(directory.Path);
        var node = Number(.3);
        var graph = Graph([node, Output()]) with
        {
            Layout = [new(node.Id, 123, 456)],
            DurationSeconds = 5
        };
        await store.SaveAsync([graph]);
        var json = await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain("PipelineSession", json);
        Assert.DoesNotContain("Nodify", json);
        Assert.Equal(JsonSerializer.Serialize(graph, PipelineJson.Options), JsonSerializer.Serialize(Assert.Single(await store.LoadAsync()), PipelineJson.Options));
        await File.WriteAllTextAsync(store.FilePath, "corrupt", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<LocalizedIOException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<LocalizedOperationException>(() => store.SaveAsync([]));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    private static async Task Wait(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++)
        {
            if (predicate())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(predicate());
    }
    private sealed class PipelineTransport : IDeviceTransport
    {
        public ConcurrentQueue<OutputFrame> Frames { get; } = new();
        private OutputFrame _last = OutputFrame.Zero;
        public OutputFrame Last => _last;
        public bool FailZero { get; set; }
        public bool FailNonzero { get; set; }
        public bool BlockNonzero { get; set; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FastWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DisplayName => "test";
        public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DeviceDescriptor>>([Device]);
        public Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
        {
            if (FailZero && frame.ChannelA == 0)
                throw new IOException("zero failed");
            if (FailNonzero && frame.ChannelA > 0)
                throw new IOException("write failed");
            if (frame.ChannelA > 0 && deviceId == "one" && BlockNonzero)
            {
                WriteStarted.TrySetResult();
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }
            _last = frame;
            Frames.Enqueue(frame);
            if (frame.ChannelA > 0 && deviceId == "fast")
                FastWrite.TrySetResult();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
