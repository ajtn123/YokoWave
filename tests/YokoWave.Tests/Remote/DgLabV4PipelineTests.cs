using System.Collections.Concurrent;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using YokoWave.Remote;
using YokoWave.Remote.DgLab.V4;
using YokoWave.Tests.Support.Bluetooth;

namespace YokoWave.Tests.Remote;

public sealed class DgLabV4PipelineTests
{
    [Fact]
    public async Task ControlledNodesShareAnAppAndRouteTasksThroughIndependentLocalLimits()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3, BluetoothProtocol.Opossum);
        await using var devices = new DeviceController(new BluetoothDeviceTransport(adapter));
        var discovered = (await devices.ScanAsync(Ct)).ToArray();
        foreach (var device in discovered)
            await devices.ConnectAsync(device, Ct);
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: Ct);
        await using var remote = new DgLabV4Peer();
        await remote.ConnectAsync(relay.Url!, Ct);
        ConcurrentBag<string> apps = [];
        remote.FrameReceived += (type, frame) =>
        {
            if (type == "client_attached")
                apps.Add(frame.GetProperty("clientId").GetString()!);
        };
        var builder = new PipelineBuilder("V4 controlled devices");
        List<NodeDefinition> inputs = [];
        foreach (var device in discovered)
        {
            var input = builder.Add(DgLabV4Nodes.App, new DgLabV4AppNode(device.Id, relay.Url!.ToString(), remote.ClientId));
            var output = builder.Add(BuiltInNodes.RouterOutput, new RouterOutputNode(device.Id, .25));
            inputs.Add(input);
            foreach (var port in BuiltInNodes.FramePorts())
                builder.Connect(input, port.Id, output, port.Id);
        }
        await using var session = new PipelineSession(devices);
        var plan = new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(builder.Build(), [], discovered.ToDictionary(device => device.Id));
        await session.StartAsync(plan);
        await AsyncAssert.EventuallyAsync(() => apps.Count == 1);
        string app = Assert.Single(apps);
        var descriptions = await remote.RequestAsync(app, "devices.get", cancellationToken: Ct);
        Assert.Equal(2, descriptions.GetProperty("devices").GetArrayLength());
        string slot = inputs[0].Id.ToString("N");
        var temporary = remote.RequestAsync(app, "device.op", new { s = slot, t = 4, c = 0, v = 160 }, cancellationToken: Ct);
        var pulse = remote.RequestAsync(app, "device.op", new { s = slot, t = 0, c = 0, v = Enumerable.Repeat("3232323264646464", 5).ToArray() }, cancellationToken: Ct);
        await AsyncAssert.EventuallyAsync(() => devices.Connections[discovered[0].Id].Output.ChannelA == 25);
        Assert.Equal(0, devices.Connections[discovered[1].Id].Output.ChannelA);
        Assert.False(temporary.IsCompleted);
        await remote.RequestAsync(app, "device.op.clear", new { s = slot, c = 0 }, cancellationToken: Ct);
        Assert.Equal("cleared", (await temporary).GetProperty("reason").GetString());
        Assert.Equal("cleared", (await pulse).GetProperty("reason").GetString());
        await AsyncAssert.EventuallyAsync(() => devices.Connections[discovered[0].Id].Output.ChannelA == 0);
        var invalid = await Assert.ThrowsAsync<DgLabV4Exception>(() => remote.RequestAsync(app, "device.op.clear", new { c = 0 }, cancellationToken: Ct));
        Assert.Equal("invalid_params", invalid.Code);
        await session.StopAsync();
        Assert.All(discovered, device => Assert.Null(devices.DeviceOwner(device.Id)));
    }

    [Fact]
    public async Task ReplacementCompletionPauseCancellationAndFeedbackUseTheSameSession()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var devices = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await devices.ScanAsync(Ct));
        await devices.ConnectAsync(device, Ct);
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: Ct);
        await using var remote = new DgLabV4Peer();
        await remote.ConnectAsync(relay.Url!, Ct);
        var attached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedback = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.FrameReceived += (type, frame) =>
        {
            if (type == "client_attached")
                attached.TrySetResult(frame.GetProperty("clientId").GetString()!);
            if (type == "message" && frame.GetProperty("data").TryGetProperty("ev", out var action) && action.GetString() == "custom.action")
                feedback.TrySetResult(frame.GetProperty("data").GetProperty("action").GetInt32());
        };
        var builder = new PipelineBuilder("V4 lifetime");
        var input = builder.Add(DgLabV4Nodes.App, new DgLabV4AppNode(device.Id, relay.Url!.ToString(), remote.ClientId));
        var output = builder.Add(BuiltInNodes.RouterOutput, new RouterOutputNode(device.Id, .2));
        foreach (var port in BuiltInNodes.FramePorts())
            builder.Connect(input, port.Id, output, port.Id);
        builder.Connect(builder.Add(BuiltInNodes.Constant, new ConstantNode(5)), "value", input, "action");
        builder.Connect(builder.Add(BuiltInNodes.Constant, new ConstantNode(1)), "value", input, "trigger");
        await using var session = new PipelineSession(devices);
        await session.StartAsync(new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(builder.Build(), [], new Dictionary<string, DeviceDescriptor> { [device.Id] = device }));
        string app = await attached.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        string slot = input.Id.ToString("N");
        Assert.Equal(5, await feedback.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        var first = remote.RequestAsync(app, "device.op", new { s = slot, t = 4, c = 0, v = 80 }, cancellationToken: Ct);
        await AsyncAssert.EventuallyAsync(() => devices.Connections[device.Id].Output.ChannelA == 20);
        var replacement = remote.RequestAsync(app, "device.op", new { s = slot, t = 4, c = 0, v = 20, d = 100, im = true }, cancellationToken: Ct);
        Assert.Equal("replaced", (await first).GetProperty("reason").GetString());
        Assert.Equal("completed", (await replacement).GetProperty("reason").GetString());
        var paused = remote.RequestAsync(app, "device.op", new { s = slot, t = 4, c = 1, v = 80 }, cancellationToken: Ct);
        await AsyncAssert.EventuallyAsync(() => devices.Connections[device.Id].Output.ChannelB == 20);
        await session.PauseAsync();
        Assert.Equal("cancelled", (await paused).GetProperty("reason").GetString());
        Assert.Equal(0, devices.Connections[device.Id].Output.ChannelB);
        Assert.Equal(session.OwnerId, devices.DeviceOwner(device.Id));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeviceSourceRoundTripUsesBothGraphsLimitsAndConfirmedStop()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV2);
        await using var hardware = new DeviceController(new BluetoothDeviceTransport(adapter));
        var local = Assert.Single(await hardware.ScanAsync(Ct));
        await hardware.ConnectAsync(local, Ct);
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: Ct);
        var source = new DgLabV4Transport(Guid.NewGuid(), relay.Url!.ToString());
        await using var controller = new DeviceController(source);
        Assert.Empty(await controller.ScanAsync(Ct));
        var appUrl = new Uri(Uri.UnescapeDataString(source.PairingUrl.Split("&url=")[1]));
        string target = appUrl.Query[5..];
        var registry = RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry());
        await using var controlled = new PipelineSession(hardware);
        var graph = DgLabV4Nodes.AppTemplate.Create(new("Controlled", local.Id, Limit: .25));
        graph = graph with
        {
            Nodes = graph.Nodes.Select(node => node.Type == DgLabV4Nodes.App
            ? NodeDefinition.Create(node.Type, new DgLabV4AppNode(local.Id, relay.Url.ToString(), target), node.Id) : node).ToArray()
        };
        await controlled.StartAsync(new PipelineCompiler(registry).Compile(graph, [], new Dictionary<string, DeviceDescriptor> { [local.Id] = local }));
        await AsyncAssert.EventuallyAsync(() => source.Slots.Count == 1);
        var remote = Assert.Single(await controller.ScanAsync(Ct));
        await controller.ConnectAsync(remote, Ct);
        var builder = new PipelineBuilder("Controller");
        var level = builder.Add(BuiltInNodes.Constant, new ConstantNode(.8));
        var output = builder.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(remote.Id, Limit: .4));
        builder.Connect(level, "value", output, "strength");
        await using var playback = new PipelineSession(controller);
        await playback.StartAsync(new PipelineCompiler(registry).Compile(builder.Build(), [], new Dictionary<string, DeviceDescriptor> { [remote.Id] = remote }));
        await AsyncAssert.EventuallyAsync(() => hardware.Connections[local.Id].Output.ChannelA == 25);
        Assert.Equal(40, controller.Connections[remote.Id].Output.ChannelA);
        await playback.StopAsync();
        await AsyncAssert.EventuallyAsync(() => hardware.Connections[local.Id].Output.ChannelA == 0);
        Assert.Null(controller.DeviceOwner(remote.Id));
        Assert.Equal(controlled.OwnerId, hardware.DeviceOwner(local.Id));
        await controller.DisconnectAsync(remote.Id, Ct);
        await controlled.StopAsync();
    }

    [Fact]
    public async Task SensorSharingEnablesStreamReportsPressureAndDisablesItOnStop()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.Civet);
        await using var devices = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await devices.ScanAsync(Ct));
        await devices.ConnectAsync(device, Ct);
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: Ct);
        await using var controller = new DgLabV4Peer();
        await controller.ConnectAsync(relay.Url!, Ct);
        var snapshot = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string> frames = new();
        Exception? wireError = null;
        controller.Disconnected += error => wireError = error;
        controller.FrameReceived += (type, frame) =>
        {
            frames.Enqueue(frame.GetRawText());
            if (type != "message")
                return;
            var data = frame.GetProperty("data");
            if (!data.TryGetProperty("devices", out var slots) && !data.TryGetProperty("slots", out slots))
                return;
            foreach (var slot in slots.EnumerateArray())
            {
                if (slot.TryGetProperty("props", out var props) && props.TryGetProperty("pressure", out var pressure))
                    snapshot.TrySetResult(pressure.GetDouble());
            }
        };
        var builder = new PipelineBuilder("Pressure sharing");
        builder.Add(DgLabV4Nodes.App, new DgLabV4AppNode(device.Id, relay.Url!.ToString(), controller.ClientId));
        await using var session = new PipelineSession(devices);
        await session.StartAsync(new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(builder.Build(), [], new Dictionary<string, DeviceDescriptor> { [device.Id] = device }));
        try
        {
            await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Pressure publication missing. State: {session.State}; telemetry: {devices.Connections[device.Id].Telemetry}; frames: {string.Join(Environment.NewLine, frames)}");
        }
        Assert.True(controller.IsConnected, wireError?.ToString());
        Assert.Null(session.Error);
        Assert.Equal(1.23, await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        await session.StopAsync();
        var stream = adapter.Links[device.Id].Writes.Where(write => write.Data[0] == 0x50).Select(write => write.Data[2]);
        Assert.Contains((byte)0xd0, stream);
        Assert.Equal(0, stream.Last());
        Assert.Null(devices.DeviceOwner(device.Id));
    }

    [Fact]
    public async Task RelayTemplateRunsWithoutDevicesAndReleasesListenerOnPauseAndStop()
    {
        await using var devices = new DeviceController(new SimulationTransport());
        var services = new PipelineServices();
        var builder = new PipelineBuilder("Relay");
        var node = builder.Add(DgLabV4Nodes.Relay, new DgLabV4RelayNode(new(Port: 0)));
        var plan = new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(builder.Build(), []);
        await using var session = new PipelineSession(devices, services);
        await session.StartAsync(plan);
        await using var peer = new DgLabV4Peer();
        await peer.ConnectAsync(new Uri(services.Endpoints[node.Id].Url), Ct);
        await session.PauseAsync();
        await AsyncAssert.EventuallyAsync(() => !peer.IsConnected);
        Assert.Empty(services.Endpoints);
        await session.PauseAsync();
        Assert.Single(services.Endpoints);
        await session.StopAsync();
        Assert.Empty(services.Endpoints);
    }
}
