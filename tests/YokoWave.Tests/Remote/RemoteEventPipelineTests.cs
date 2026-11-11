using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using YokoWave.Remote;
using YokoWave.Tests.Support.Bluetooth;
using static YokoWave.Tests.Support.Pipelines.PipelineTestGraphs;

namespace YokoWave.Tests.Remote;

public sealed class RemoteEventPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TriggerBurstsRetainSeparateEdgesAndDisconnectClearsPendingEvents()
    {
        var (runtime, source, input) = Runtime("collision");
        var registration = Guid.NewGuid();
        var messages = Enumerable.Range(1, 3).Select(index => new EventValue("game", "collision", registration, true, 1, index, EvaluationTime, EvaluationTime.AddSeconds(5), false)).ToArray();
        runtime.Inputs.Events[source] = new([new("game", registration)], messages);
        Assert.Equal(100, Level(runtime, 0));
        runtime.Inputs.Events[source] = new([new("game", registration)], []);
        Assert.Equal(0, Level(runtime, .1));
        Assert.Equal(100, Level(runtime, .2));
        Assert.Equal(0, Level(runtime, .3));
        Assert.Equal(100, Level(runtime, .4));
        Assert.Equal(3, runtime.NodeStatus[input].Count);
        runtime.Inputs.Events[source] = EventSignal.Empty;
        Assert.Equal(0, Level(runtime, .5));
        Assert.Equal(0, runtime.NodeStatus[input].Count);
    }

    [Fact]
    public void NumericUpdatesUseLatestValueExpireAndPropagateSimulatedProvenance()
    {
        var (runtime, source, input) = Runtime("score");
        var registration = Guid.NewGuid();
        EventValue Number(double value, long sequence, bool simulated = false) => new("game", "score", registration, false, value, sequence, EvaluationTime, EvaluationTime.AddSeconds(1), simulated);
        runtime.Inputs.Events[source] = new([new("game", registration)], [Number(.2, 1), Number(.8, 2)]);
        Assert.Equal(80, Level(runtime, 0));
        Assert.Equal(2, runtime.NodeStatus[input].Count);
        runtime.Inputs.Events[source] = new([new("game", registration)], []);
        Assert.Equal(80, Level(runtime, .5));
        Assert.Equal(0, Level(runtime, 1.1));
        runtime.Inputs.Events[source] = new([new("game", registration)], [Number(.5, 3, true)]);
        Assert.Equal(0, Level(runtime, 0));
        runtime.Update(NodeControl.Create(input, "selectEvent", new EventInputNode("game", "other")));
        Assert.Equal(0, Level(runtime, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedCapturesDoNotReplayConsumedEvents(bool trigger)
    {
        string eventId = trigger ? "collision" : "score";
        var (runtime, source, input) = Runtime(eventId);
        var registration = Guid.NewGuid();
        runtime.Inputs.Events[source] = new([new("game", registration)],
            [new("game", eventId, registration, trigger, trigger ? 1 : .5, 1, EvaluationTime, EvaluationTime.AddSeconds(1), false)]);
        Assert.Equal(trigger ? 100 : 50, Level(runtime, 0));
        Assert.Equal(trigger ? 0 : 50, Level(runtime, .1));
        Assert.Equal(trigger ? 0 : 50, Level(runtime, .2));
        Assert.Equal(1, runtime.NodeStatus[input].Count);
    }

    [Fact]
    public void ExpiredPendingBurstsLeaveRoomForFreshEvents()
    {
        var (runtime, source, input) = Runtime("collision");
        var registration = Guid.NewGuid();
        EventValue Trigger(int sequence, double captured, double expires) => new("game", "collision", registration, true, 1, sequence,
            EvaluationTime.AddSeconds(captured), EvaluationTime.AddSeconds(expires), false);
        runtime.Inputs.Events[source] = new([new("game", registration)], Enumerable.Range(1, 256).Select(index => Trigger(index, 0, 1)).ToArray());
        Assert.Equal(100, Level(runtime, 0));
        runtime.Inputs.Events[source] = new([new("game", registration)], [Trigger(257, 2, 3), Trigger(258, 2, 3)]);
        Assert.Equal(0, Level(runtime, 2));
        runtime.Inputs.Events[source] = new([new("game", registration)], []);
        Assert.Equal(100, Level(runtime, 2.1));
        Assert.Equal(2, runtime.NodeStatus[input].Count);
    }

    [Fact]
    public async Task NetworkEventsReachNormalGraphOutputsWithLimitsPauseAndDisconnectCleanup()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var devices = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await devices.ScanAsync(Ct));
        await devices.ConnectAsync(device, Ct);
        var graph = new PipelineBuilder("Event driven output");
        var server = graph.Add(RemoteEventNodes.Server, new EventServerNode(new(Port: 0)));
        var input = graph.Add(RemoteEventNodes.Input, new EventInputNode("game", "score"));
        var gain = graph.Add(BuiltInNodes.Gain, new GainNode(.01));
        var output = graph.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(device.Id, Limit: .25));
        graph.Connect(server, "events", input, "events");
        graph.Connect(input, "value", gain, "value");
        graph.Connect(gain, "value", output, "strength");
        var services = new PipelineServices();
        await using var session = new PipelineSession(devices, services);
        await session.StartAsync(new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(graph.Build(), [], new Dictionary<string, DeviceDescriptor> { [device.Id] = device }));
        string url = services.Endpoints[server.Id].Url;
        await using var client = new RemoteEventClient(url, services.Credentials[server.Id].Token,
            new("game", [new("score", RemoteEventKind.Number, Minimum: 0, Maximum: 100, ValidityMilliseconds: 5000)]));
        await client.ConnectAsync(Ct);
        await client.EmitAsync("score", 80, Ct);
        await AsyncAssert.EventuallyAsync(() => devices.Connections[device.Id].Output.ChannelA == 25);
        Assert.Single(RemoteEventRuntime.For(services).Source(server.Id).Snapshot());
        await session.PauseAsync();
        Assert.Equal(0, devices.Connections[device.Id].Output.ChannelA);
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.EmitAsync("score", 90, Ct));
        await session.PauseAsync();
        Assert.Equal(0, devices.Connections[device.Id].Output.ChannelA);
        await client.EmitAsync("score", 10, Ct);
        await AsyncAssert.EventuallyAsync(() => devices.Connections[device.Id].Output.ChannelA == 10);
        await client.DisposeAsync();
        await AsyncAssert.EventuallyAsync(() => devices.Connections[device.Id].Output.ChannelA == 0);
        await session.StopAsync();
        Assert.Null(devices.DeviceOwner(device.Id));
        Assert.Empty(services.Endpoints);
    }

    [Fact]
    public async Task EventTemplateRunsWithoutDevicesAndTypedPortsRejectNumericSources()
    {
        var graph = RemoteEventNodes.Template.Create(new("Events"));
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Type == RemoteEventNodes.Server ? NodeDefinition.Create(node.Type, new EventServerNode(new(Port: 0)), node.Id) : node).ToArray() };
        var registry = RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry());
        await using var devices = new DeviceController(new SimulationTransport());
        var services = new PipelineServices();
        await using var session = new PipelineSession(devices, services);
        await session.StartAsync(new PipelineCompiler(registry).Compile(graph, []));
        Assert.Single(services.Endpoints);
        await session.StopAsync();
        var invalid = new PipelineBuilder("Invalid");
        var number = invalid.Add(BuiltInNodes.Constant, new ConstantNode(1));
        var input = invalid.Add(RemoteEventNodes.Input, new EventInputNode());
        invalid.Connect(number, "value", input, "events");
        Assert.Contains(new PipelineCompiler(registry).Validate(invalid.Build(), []), diagnostic => diagnostic.Key == "Pipeline.Error.Connection");
    }

    [Fact]
    public void TypedEventStreamsFanOutAcrossIndependentSubgraphInstances()
    {
        var leaf = new PipelineBuilder("Reaction");
        var input = leaf.Add(RemoteEventNodes.Input, new EventInputNode("game", "score"));
        var output = leaf.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", Limit: 1));
        leaf.Connect(input, "value", output, "strength");
        ExposedPort[] inputs = [new("events", "Pipeline.Port.Events", new(input.Id, "events"), Kind: PortValueKind.Event)];
        var root = new PipelineBuilder("Shared event stream");
        var source = root.Add(RemoteEventNodes.Server, new EventServerNode(new(Port: 0)));
        foreach (string deviceId in new[] { "one", "two" })
        {
            var wrapper = root.Add(SubgraphExpansion.Type, new SubgraphNode(leaf.Build(), inputs, [], new() { ["out"] = deviceId }));
            root.Connect(source, "events", wrapper, "events");
        }
        var runtime = new PipelineRuntime(new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(root.Build(), []), new HashSet<string>());
        var registration = Guid.NewGuid();
        runtime.Inputs.Events[source.Id] = new([new("game", registration)], [new("game", "score", registration, false, .6, 1, EvaluationTime, EvaluationTime.AddSeconds(1), false)]);
        var frame = runtime.Evaluate(TimeSpan.Zero, EvaluationTime);
        Assert.Equal(60, frame.Outputs["one"].ChannelA);
        Assert.Equal(60, frame.Outputs["two"].ChannelA);
        Assert.Equal(2, runtime.NodeStatus.Values.Count(status => status.Count == 1));
    }

    private static (PipelineRuntime Runtime, Guid Source, Guid Input) Runtime(string eventId)
    {
        var graph = new PipelineBuilder("Events", 1);
        var source = graph.Add(RemoteEventNodes.Server, new EventServerNode(new(Port: 0)));
        var input = graph.Add(RemoteEventNodes.Input, new EventInputNode("game", eventId));
        var output = graph.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", Limit: 1));
        graph.Connect(source, "events", input, "events");
        graph.Connect(input, "value", output, "strength");
        return (new(new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(graph.Build(), []), new HashSet<string>()), source.Id, input.Id);
    }
    private static double Level(PipelineRuntime runtime, double seconds) => runtime.Evaluate(TimeSpan.FromSeconds(seconds), EvaluationTime.AddSeconds(seconds)).Outputs["out"].ChannelA;
}
