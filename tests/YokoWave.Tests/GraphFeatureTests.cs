using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class GraphFeatureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T00:00:00Z");
    private static WaveformPreset Wave(double frequency = 20, double seconds = 1) => new(Guid.NewGuid(), "wave", WaveShape.Custom, seconds, frequency, [1, 1]);
    private static PipelineRuntime Runtime(PipelineBuilder graph, params WaveformPreset[] waves) => new(
        new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph.Build(), waves), new HashSet<string> { "out", "sensor" });
    private static NodeDefinition Sink(PipelineBuilder b, NodeDefinition source, string port = "value", int channel = 0, double limit = .6)
    {
        var output = b.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", channel, Limit: limit));
        b.Connect(source, port, output, "strength");
        return output;
    }
    private static double Level(PipelineRuntime runtime, double seconds = 0) => runtime.Evaluate(TimeSpan.FromSeconds(seconds), Now.AddSeconds(seconds)).Outputs["out"].ChannelA;
    public static IEnumerable<object[]> Templates => PipelineTemplates.All.Select(t => new object[] { t.Key });

    [Theory]
    [InlineData(BluetoothProtocol.YokonexV1)]
    [InlineData(BluetoothProtocol.YokonexV2)]
    [InlineData(BluetoothProtocol.YokonexMotor)]
    [InlineData(BluetoothProtocol.CoyoteV2)]
    [InlineData(BluetoothProtocol.CoyoteV3)]
    [InlineData(BluetoothProtocol.Opossum)]
    public async Task EveryDefaultWaveformFlowsThroughEveryElectricalAndMotorDriver(BluetoothProtocol protocol)
    {
        var adapter = new HardwareGattAdapter(protocol);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(TestContext.Current.CancellationToken));
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        foreach (var waveform in WaveformPreset.BuiltIn)
        {
            var saved = JsonSerializer.Serialize(waveform);
            var graph = PipelineTemplates.Create("waveform", new("wave", device.Id, Waveform: waveform, Channels: device.ChannelCount));
            var runtime = new PipelineRuntime(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, WaveformPreset.BuiltIn,
                new Dictionary<string, DeviceDescriptor> { [device.Id] = device }), new HashSet<string>());
            foreach (var node in graph.Nodes.Where(n => n.Type == BuiltInNodes.Constant))
                runtime.SetConstant(node.Id, .4);
            var frame = runtime.Evaluate(TimeSpan.FromSeconds(waveform.PeriodSeconds / 2), Now).Outputs[device.Id];
            Assert.Equal(4, frame.WindowA!.Count);
            await controller.SendAsync(device.Id, frame, TestContext.Current.CancellationToken);
            Assert.Equal(saved, JsonSerializer.Serialize(waveform));
            Assert.InRange(controller.Connections[device.Id].Output.ChannelA, 0, 40);
        }
        await controller.StopAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, controller.Connections[device.Id].Output.ChannelA);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryPresetIsAnOrdinaryGraphWithFreshIndependentState(string key)
    {
        var graph = PipelineTemplates.Create(key, new("template", "out", "sensor", WaveformPreset.BuiltIn[0]));
        Assert.NotEmpty(graph.Nodes);
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, WaveformPreset.BuiltIn);
        var first = new PipelineRuntime(plan, new HashSet<string> { "out", "sensor" });
        var next = new PipelineRuntime(plan, new HashSet<string> { "out", "sensor" });
        var initial = JsonSerializer.Serialize(first.Evaluate(TimeSpan.Zero, Now));
        foreach (var node in graph.Nodes.Where(n => n.Type == BuiltInNodes.Constant))
            first.SetConstant(node.Id, .8);
        first.Evaluate(TimeSpan.FromSeconds(1), Now.AddSeconds(1));
        Assert.Equal(initial, JsonSerializer.Serialize(next.Evaluate(TimeSpan.Zero, Now)));
        Assert.All(first.Evaluate(TimeSpan.Zero, Now).Outputs.Values, f => Assert.InRange(f.ChannelA, 0, 60));
    }

    [Theory]
    [InlineData(PlaybackOrder.Repeat)]
    [InlineData(PlaybackOrder.Once)]
    [InlineData(PlaybackOrder.Sequential)]
    [InlineData(PlaybackOrder.Random)]
    public void PlaylistsHonorOrderAndCompletionAndPreserveOtherChannelOnLiveChange(PlaybackOrder order)
    {
        var a = Wave(10);
        var bWave = Wave(70);
        var b = new PipelineBuilder("playlist", 1);
        var playlist = b.Add(BuiltInNodes.Playlist, new PlaylistNode(a.Id, [a.Id, bWave.Id], order));
        var other = b.Add(BuiltInNodes.Playlist, new PlaylistNode(a.Id));
        Sink(b, playlist, "levelA");
        Sink(b, other, "levelA", 1);
        var runtime = Runtime(b, a, bWave);
        Assert.Equal(60, Level(runtime));
        var frame = runtime.Evaluate(TimeSpan.FromSeconds(1), Now.AddSeconds(1));
        if (order == PlaybackOrder.Once)
        {
            Assert.Equal(0, frame.Outputs["out"].ChannelA);
            Assert.Equal("Reason.WaveComplete", runtime.CompletionReason);
        }
        else if (order != PlaybackOrder.Repeat)
            Assert.Equal(bWave.Id, runtime.NodeStatus[playlist.Id].Waveform);
        runtime.Update(NodeControl.Create(playlist.Id, "waveform", bWave));
        runtime.Evaluate(TimeSpan.FromSeconds(1.1), Now.AddSeconds(1.1));
        Assert.Equal(bWave.Id, runtime.NodeStatus[playlist.Id].Waveform);
        Assert.Equal(a.Id, runtime.NodeStatus[other.Id].Waveform);
    }

    [Theory]
    [InlineData(TouchPlayback.Momentary, false)]
    [InlineData(TouchPlayback.HoldLast, true)]
    [InlineData(TouchPlayback.RecordedLoop, true)]
    public void TouchReleaseRecordingAndPlaybackModesRemainAvailable(TouchPlayback mode, bool remainsActive)
    {
        var b = new PipelineBuilder("touch", 1);
        var touch = b.Add(BuiltInNodes.Touch, new TouchNode(mode));
        var sink = Sink(b, touch, "level");
        b.Connect(touch, "frequency", sink, "frequency");
        b.Connect(touch, "pulseWidth", sink, "pulseWidth");
        var runtime = Runtime(b);
        Assert.Equal(0, Level(runtime));
        runtime.Update(NodeControl.Create(touch.Id, "touch", new TouchPoint(75, .6, true)));
        Assert.Equal(60, Level(runtime, .1));
        Level(runtime, .2);
        runtime.Update(NodeControl.Create(touch.Id, "touch", new TouchPoint(75, .6, false)));
        Assert.Equal(remainsActive ? 60 : 0, Level(runtime, .3));
        Assert.Equal(2, runtime.NodeStatus[touch.Id].Recording!.Count);
        for (var i = 0; i < 300; i++)
        {
            runtime.Update(NodeControl.Create(touch.Id, "touch", new TouchPoint(75, .6, true)));
            Level(runtime, 1 + i / 10d);
        }
        Assert.Equal(256, runtime.NodeStatus[touch.Id].Recording!.Count);
        Assert.ThrowsAny<ArgumentException>(() =>
        {
            runtime.Update(NodeControl.Create(touch.Id, "touch", new TouchPoint(double.NaN, .6, true)));
            Level(runtime, 32);
        });
    }

    [Theory]
    [InlineData(AudioFollowMode.Volume)]
    [InlineData(AudioFollowMode.Rhythm)]
    public void AudioStereoSilenceAndStaleFramesWaitAndRecover(AudioFollowMode follow)
    {
        var b = new PipelineBuilder("audio", 1);
        var audio = b.Add(BuiltInNodes.Audio, new AudioNode(new(Follow: follow)));
        Sink(b, audio, "left");
        Sink(b, audio, "right", 1);
        var runtime = Runtime(b);
        Assert.Equal(0, Level(runtime));
        runtime.Inputs.Audio[audio.Id] = new(1, Now, -6, -120, 1, 0);
        var active = runtime.Evaluate(TimeSpan.Zero, Now).Outputs["out"];
        Assert.True(active.ChannelA > 0);
        Assert.Equal(0, active.ChannelB);
        Assert.Equal(0, Level(runtime, 3));
        Assert.Equal("Waiting", runtime.NodeStatus[audio.Id].Phase);
        runtime.Inputs.Audio[audio.Id] = new(2, Now.AddSeconds(3), -6, -120, 1, 0);
        Assert.True(Level(runtime, 3) > 0);
        runtime.Inputs.Audio[audio.Id] = new(3, Now.AddSeconds(3), double.NaN, -6, 1, 1);
        Assert.Equal(0, Level(runtime, 3));
    }

    [Fact]
    public void RestraintUsesPerRunBaselineCalibrationAndFreshness()
    {
        var b = new PipelineBuilder("restraint", 1);
        var input = b.Add(BuiltInNodes.Sensor, new SensorNode("sensor"));
        var deviation = b.Add(BuiltInNodes.Deviation, new DeviationNode());
        var map = b.Add(BuiltInNodes.RangeMap, new RangeMapNode(5, 35));
        b.Connect(input, "angle", deviation, "value");
        b.Connect(deviation, "value", map, "value");
        Sink(b, map);
        var runtime = Runtime(b);
        void Motion(double angle, double time) => runtime.Inputs.Sensors[input.Id] = new(new("sensor", "sensor", DeviceKind.Sensor, 80, -40),
            new(angle, (long)(time * 10), Now.AddSeconds(time)), null, null, null, true);
        Motion(10, 0);
        Assert.Equal(0, Level(runtime));
        Motion(45, 1);
        Assert.Equal(60, Level(runtime, 1));
        runtime.Update(NodeControl.Create(deviation.Id, "calibrate", true));
        Assert.Equal(0, Level(runtime, 1));
        Motion(80, 2);
        Assert.Equal(60, Level(runtime, 2));
        Assert.Equal(0, Level(runtime, 5));
    }

    [Fact]
    public void ExerciseCountsFreshMovementAndStepsAndStopsAtGoal()
    {
        var b = new PipelineBuilder("exercise", 1);
        var input = b.Add(BuiltInNodes.Sensor, new SensorNode("sensor"));
        var count = b.Add(BuiltInNodes.Counter, new CounterNode(10, 2));
        b.Connect(input, "angle", count, "angle");
        b.Connect(input, "steps", count, "steps");
        var runtime = Runtime(b);
        void Motion(double angle, int steps, int second)
        {
            runtime.Inputs.Sensors[input.Id] = new(new("sensor", "sensor", DeviceKind.Sensor, 80, -40), new(angle, second + 1, Now.AddSeconds(second), steps), steps, null, null, true);
            runtime.Evaluate(TimeSpan.FromSeconds(second), Now.AddSeconds(second));
        }
        Motion(10, 0, 0);
        Motion(40, 0, 1);
        Assert.Equal(1, runtime.NodeStatus[count.Id].Count);
        runtime.Evaluate(TimeSpan.FromSeconds(1.8), Now.AddSeconds(1.8));
        Assert.Equal(1, runtime.NodeStatus[count.Id].Count);
        Motion(40, 1, 2);
        Assert.Equal(2, runtime.NodeStatus[count.Id].Count);
        Assert.Equal("Reason.MovementGoal", runtime.CompletionReason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SleepCyclesWaitBurstAndDoNotReplayMissedBursts(bool random, bool activity)
    {
        var b = new PipelineBuilder("cycle", 1);
        var input = b.Add(BuiltInNodes.SimulatedInput, new SimulatedInputNode());
        var cycle = b.Add(BuiltInNodes.Cycle, new CycleNode(2, 1, activity, activity, random, 2, 2, 1, 1, .5, .5));
        b.Connect(input, "value", cycle, "activity");
        Sink(b, cycle, "gate");
        var runtime = Runtime(b);
        Assert.Equal(0, Level(runtime));
        Level(runtime, 1);
        Assert.Equal(random ? 50 : 60, Level(runtime, 2));
        Assert.Equal(0, Level(runtime, 3));
        Assert.Equal(0, Level(runtime, 30));
        Assert.Equal("Resting", runtime.NodeStatus[cycle.Id].Phase);
        runtime.Suspend();
        Assert.Equal(0, Level(runtime, 30));
    }

    [Theory]
    [InlineData(StrengthMode.Fixed)]
    [InlineData(StrengthMode.StepIncrease)]
    [InlineData(StrengthMode.RandomIncrease)]
    public void StrengthProgressionBoostResetAndZeroChannelsRespectLimits(StrengthMode mode)
    {
        var b = new PipelineBuilder("strength", .5);
        var input = b.Add(BuiltInNodes.Constant, new ConstantNode(.3));
        var automation = b.Add(BuiltInNodes.Automation, new AutomationNode(new(mode, 1, 20, 10, 50, 20)));
        b.Connect(input, "value", automation, "value");
        Sink(b, automation, limit: .5);
        var runtime = Runtime(b);
        Assert.Equal(30, Level(runtime));
        Assert.InRange(Level(runtime, 2), 30, 50);
        runtime.Update(NodeControl.Create(automation.Id, "boost", true));
        Assert.Equal(50, Level(runtime, 2));
        runtime.Update(NodeControl.Create(automation.Id, "reset", true));
        Assert.Equal(30, Level(runtime, 2));
        runtime.SetConstant(input.Id, 0);
        runtime.Update(NodeControl.Create(automation.Id, "boost", true));
        Assert.Equal(0, Level(runtime, 2));
    }

    [Fact]
    public void PumpTimersAreIndependentSupportContinuousAndLiveChanges()
    {
        var b = new PipelineBuilder("pump");
        var timer = b.Add(BuiltInNodes.PumpTimer, new PumpTimerNode(new(PumpDirection.Forward, true, 2, 3)));
        var sink = b.Add(BuiltInNodes.PumpOutput, new PumpOutputNode("out"));
        foreach (var port in new[] { "direction", "water", "peristalticSeconds", "waterSeconds" })
            b.Connect(timer, port, sink, port);
        var runtime = Runtime(b);
        PumpOutput Output(double time) => runtime.Evaluate(TimeSpan.FromSeconds(time), Now.AddSeconds(time)).Outputs["out"].Pump!;
        Assert.Equal(new(PumpDirection.Forward, true, 2, 3), Output(0));
        Assert.Equal(PumpDirection.Off, Output(2).Direction);
        Assert.True(Output(2).Water);
        Assert.False(Output(3).Water);
        Assert.Equal("Reason.PumpComplete", runtime.CompletionReason);
        runtime.Update(NodeControl.Create(timer.Id, "pump", new PumpOutput(PumpDirection.Reverse, false)));
        Assert.Equal(PumpDirection.Reverse, Output(4).Direction);
        Assert.Equal(PumpDirection.Reverse, Output(100).Direction);
        Assert.Null(runtime.CompletionReason);
    }

    [Theory]
    [InlineData(NativeProgramKind.ElectricalPreset)]
    [InlineData(NativeProgramKind.ElectricalBuffered)]
    [InlineData(NativeProgramKind.CoyoteV2Pulse)]
    [InlineData(NativeProgramKind.MotorPreset)]
    public void NativeProgramsRetainAllProtocolSettings(NativeProgramKind kind)
    {
        var b = new PipelineBuilder("native", 1);
        var strength = b.Add(BuiltInNodes.Constant, new ConstantNode(kind == NativeProgramKind.MotorPreset ? 1 : .4));
        var program = new NativeProgramSettings(kind, BufferA: [new()], BufferB: [new()], PulseA: new(4, 500, 7));
        var sink = b.Add(BuiltInNodes.NativeOutput, new NativeOutputNode("out", program, Limit: 1));
        b.Connect(strength, "value", sink, "strengthA");
        var frame = Runtime(b).Evaluate(TimeSpan.Zero, Now).Outputs["out"];
        Assert.Equal(kind, frame.NativeProgram!.Settings.Kind);
        Assert.Equivalent(program, frame.NativeProgram.Settings);
        Assert.Equal(kind == NativeProgramKind.MotorPreset ? 100 : 40, frame.ChannelA);
    }

    [Fact]
    public void SubgraphInstancesHaveIndependentIdsResourcesPortsAndDeviceMappings()
    {
        var inner = new PipelineBuilder("inner", 1);
        var value = inner.Add(BuiltInNodes.Constant, new ConstantNode(.4));
        Sink(inner, value);
        var graph = inner.Build();
        var b = new PipelineBuilder("outer", 1);
        var first = b.Add(SubgraphExpansion.Type, new SubgraphNode(graph, [], [new("value", "Pipeline.Port.Value", new(value.Id, "value"))], new()
        {
            ["out"] = "one"
        }));
        b.Add(SubgraphExpansion.Type, new SubgraphNode(graph, [], [], new()
        {
            ["out"] = "two"
        }));
        var expanded = SubgraphExpansion.Expand(b.Build());
        Assert.Equal(4, expanded.Nodes.Select(n => n.Id).Distinct().Count());
        var runtime = Runtime(b);
        var outputs = runtime.Evaluate(TimeSpan.Zero, Now).Outputs;
        Assert.Equal(40, outputs["one"].ChannelA);
        Assert.Equal(40, outputs["two"].ChannelA);
        var rebound = SubgraphExpansion.MapDevices(first, new Dictionary<string, string> { ["one"] = "three" });
        Assert.Equal("three", rebound.Configuration.GetProperty("Devices").GetProperty("out").GetString());
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
        NodeDefinition processor = type switch
        {
            BuiltInNodes.Gain => b.Add(type, new GainNode()),
            BuiltInNodes.Limit => b.Add(type, new LimitNode()),
            BuiltInNodes.Smooth => b.Add(type, new SmoothNode()),
            _ => b.Add(type, new ThresholdNode(.4))
        };
        b.Connect(input, "value", processor, "value");
        Sink(b, processor);
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(b.Build(), []);
        Assert.Equal(0, Level(new(plan, new HashSet<string>())));
        Assert.True(Level(new(plan, new HashSet<string> { "out" })) > 0);
    }

    [Theory]
    [InlineData(AudioSourceKind.Microphone)]
    [InlineData(AudioSourceKind.SystemAudio)]
    [InlineData(AudioSourceKind.MediaFile)]
    public async Task AudioResourcesListenAcrossGapsMuteAndEofAndReleaseAfterStop(AudioSourceKind source)
    {
        var audio = new FakeAudioInputFactory();
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        await controller.ConnectAsync(new("out", "out", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        var graph = PipelineTemplates.Create("audio", new("audio", "out"));
        var input = graph.Nodes.Single(n => n.Type == BuiltInNodes.Audio);
        graph = graph with
        {
            Nodes = graph.Nodes.Select(n => n.Id == input.Id ? NodeDefinition.Create(n.Type, new AudioNode(new(Source: source, MediaPath: "test.wav", PlaybackVolume: 0)), n.Id) : n).ToArray()
        };
        await using var session = new PipelineSession(controller, new(audio));
        await session.StartAsync(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, []));
        var capture = Assert.Single(audio.Sessions);
        Assert.Equal(0, Assert.Single(audio.Requests).PlaybackVolume);
        capture.NoFrames = true;
        capture.Ended = true;
        await session.FlushAsync();
        Assert.Equal(PipelineState.Running, session.State);
        Assert.False(capture.Disposed);
        capture.NoFrames = false;
        capture.Ended = false;
        await session.FlushAsync();
        Assert.Equal("Tracking", session.NodeStatus[input.Id].Phase);
        await session.PauseAsync();
        Assert.True(capture.Paused);
        Assert.Equal(session.OwnerId, controller.DeviceOwner("out"));
        await session.PauseAsync();
        await session.StopAsync();
        Assert.True(capture.Disposed);
        Assert.Null(controller.DeviceOwner("out"));
    }

    [Fact]
    public async Task EmergencyStopCancelsPendingInputStartupAndReleasesReservation()
    {
        var audio = new FakeAudioInputFactory { BlockStart = true };
        await using var controller = new DeviceController(new RecordingTransport());
        await controller.ConnectAsync(new("out", "out", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        await using var session = new PipelineSession(controller, new(audio));
        var graph = PipelineTemplates.Create("audio", new("audio", "out"));
        var start = session.StartAsync(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, []));
        await audio.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.Null(controller.DeviceOwner("out"));
    }

    [Fact]
    public async Task PausedSessionStopsAllItsTargetsOnTransportFaultWithoutAnyUi()
    {
        var transport = new ConcurrentTransport();
        await using var controller = new DeviceController(transport);
        foreach (var id in new[] { "one", "two" })
            await controller.ConnectAsync(new(id, id, DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        var b = new PipelineBuilder("fault");
        var input = b.Add(BuiltInNodes.Constant, new ConstantNode(.3));
        foreach (var id in new[] { "one", "two" })
        {
            var output = b.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(id));
            b.Connect(input, "value", output, "strength");
        }
        await using var session = new PipelineSession(controller);
        await session.StartAsync(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(b.Build(), []));
        await session.PauseAsync();
        var original = new IOException("transport fault");
        transport.ReportFault(new("one", true, original));
        for (var i = 0; i < 100 && session.State != PipelineState.Idle; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(PipelineState.Idle, session.State);
        Assert.Same(original, session.Error);
        Assert.Null(controller.DeviceOwner("two"));
        Assert.Equal(0, transport.Outputs["two"].ChannelA);
    }

    [Fact]
    public void WaveformTemplatePreservesIndependentAssetChannels()
    {
        var waveform = new WaveformPreset(Guid.NewGuid(), "channels", WaveShape.Sampled, 1, 20,
            Samples: [new(1, 20, 1, [-1, .5]), new(1, 20, 1, [-1, .5])]);
        var graph = PipelineTemplates.Create("waveform", new("channels", "out", Waveform: waveform, Limit: 1));
        var runtime = new PipelineRuntime(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, [waveform]), new HashSet<string>());
        foreach (var node in graph.Nodes.Where(n => n.Type == BuiltInNodes.Constant))
            runtime.SetConstant(node.Id, .4);
        var frame = runtime.Evaluate(TimeSpan.Zero, Now).Outputs["out"];
        Assert.Equal(40, frame.ChannelA);
        Assert.Equal(20, frame.ChannelB);
    }

    [Fact]
    public void ExposedSubgraphDefaultsApplyAndCanBeOverriddenByTheOuterGraph()
    {
        var inner = new PipelineBuilder("inner", 1);
        var output = inner.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out"));
        var b = new PipelineBuilder("outer", 1);
        var sub = b.Add(SubgraphExpansion.Type, new SubgraphNode(inner.Build(),
            [new("strength", "Pipeline.Port.Strength", new(output.Id, "strength"), new("ratio", 0, 1, .3))], []));
        Assert.Equal(30, Level(Runtime(b)));
        var value = b.Add(BuiltInNodes.Constant, new ConstantNode(.4));
        b.Connect(value, "value", sub, "strength");
        Assert.Equal(40, Level(Runtime(b)));
    }

    [Fact]
    public void MovementCounterAcceptsStepOnlyNumericInput()
    {
        var b = new PipelineBuilder("steps");
        var steps = b.Add(BuiltInNodes.Constant, new ConstantNode());
        var counter = b.Add(BuiltInNodes.Counter, new CounterNode(10, 2));
        b.Connect(steps, "value", counter, "steps");
        var runtime = Runtime(b);
        runtime.Evaluate(TimeSpan.Zero, Now);
        runtime.SetConstant(steps.Id, 1);
        runtime.Evaluate(TimeSpan.FromSeconds(1), Now.AddSeconds(1));
        Assert.Equal(1, runtime.NodeStatus[counter.Id].Count);
        runtime.SetConstant(steps.Id, 2);
        runtime.Evaluate(TimeSpan.FromSeconds(2), Now.AddSeconds(2));
        Assert.Equal("Reason.MovementGoal", runtime.CompletionReason);
    }
}
