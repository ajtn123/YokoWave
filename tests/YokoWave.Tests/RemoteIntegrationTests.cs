using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Remote;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class RemoteIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(BluetoothProtocol.YokonexV1)]
    [InlineData(BluetoothProtocol.YokonexV2)]
    [InlineData(BluetoothProtocol.YokonexMotor)]
    [InlineData(BluetoothProtocol.YokonexPump)]
    [InlineData(BluetoothProtocol.CoyoteV2)]
    [InlineData(BluetoothProtocol.CoyoteV3)]
    [InlineData(BluetoothProtocol.Opossum)]
    [InlineData(BluetoothProtocol.Paw)]
    [InlineData(BluetoothProtocol.Civet)]
    public async Task HighLevelClientPreservesDeviceIdentityStatusAndConfirmedStop(BluetoothProtocol protocol)
    {
        await using var host = await Host.Create(protocol);
        await using var transport = new WebSocketControlTransport(new(Guid.NewGuid(), "control", host.Server.Url!.ToString(), RemoteConnectionKind.Control), Host.Token);
        var device = Assert.Single(await transport.ScanAsync(Ct));
        Assert.Equal(protocol.ToString(), device.ProtocolId);
        Assert.Equal(DeviceOrigin.Remote, device.Origin);
        await transport.ConnectAsync(device, Ct);
        if (!device.IsInput)
        {
            var frame = device.Kind == DeviceKind.Pump ? OutputFrame.Zero with
            {
                Pump = new(PumpDirection.Reverse, true)
            } : new OutputFrame(30, 20, 25, ChannelC: 10);
            await transport.SendAsync(device.Id, frame, Ct);
            if (device.Kind == DeviceKind.Pump)
                Assert.Equal(PumpDirection.Reverse, host.Controller.Connections[host.Devices[0].Id].Output.Pump!.Direction);
            else
                Assert.Equal(30, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
            await transport.SendAsync(device.Id, OutputFrame.Zero, Ct);
        }
        await transport.DisconnectAsync(device.Id, Ct);
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.False(host.Adapter.Links[host.Devices[0].Id].Closed);
    }

    [Fact]
    public async Task ControlServerNodeRoutesThroughLocalNumericCapsAndRevokesOnPause()
    {
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
        var device = Assert.Single(await controller.ScanAsync(Ct));
        await controller.ConnectAsync(device, Ct);
        var graph = RemotePipelineNodes.ControlTemplate.Create(new("control graph", device.Id, Limit: .25));
        var serverNode = graph.Nodes.Single(n => n.Type == RemotePipelineNodes.ControlServer);
        graph = graph with
        {
            Nodes = graph.Nodes.Select(n => n.Id == serverNode.Id
            ? NodeDefinition.Create(n.Type, new ControlServerNode(device.Id, new(Port: 0, LeaseMilliseconds: 1000)), n.Id) : n).ToArray()
        };
        var services = new PipelineServices();
        services.Credentials[serverNode.Id] = new(Host.Token);
        await using var session = new PipelineSession(controller, services);
        await session.StartAsync(new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(graph, [], new Dictionary<string, DeviceDescriptor> { [device.Id] = device }));
        var endpoint = services.Endpoints[serverNode.Id];
        await using var client = new WebSocketControlTransport(new(Guid.NewGuid(), "client", endpoint.Url, RemoteConnectionKind.Control), Host.Token);
        var remote = Assert.Single(await client.ScanAsync(Ct));
        await client.ConnectAsync(remote, Ct);
        await client.SendAsync(remote.Id, new(80, 70, 20), Ct);
        await Wait(() => controller.Connections[device.Id].Output.ChannelA == 25);
        Assert.Equal(session.OwnerId, controller.DeviceOwner(device.Id));
        session.SetLimit(graph.Nodes.Single(n => n.Type == BuiltInNodes.RouterOutput).Id, .1);
        await session.FlushAsync();
        Assert.Equal(10, controller.Connections[device.Id].Output.ChannelA);
        await session.PauseAsync();
        Assert.Equal(0, controller.Connections[device.Id].Output.ChannelA);
        Assert.Equal(session.OwnerId, controller.DeviceOwner(device.Id));
        await session.PauseAsync();
        await session.StopAsync();
        Assert.Empty(services.Endpoints);
        Assert.Null(controller.DeviceOwner(device.Id));
        Assert.False(adapter.Links[device.Id].Closed);
    }

    [Fact]
    public async Task HighLevelClientCarriesNativeOutputFramesThroughTheSamePipeline()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV2);
        await using var client = new WebSocketControlTransport(new(Guid.NewGuid(), "native", host.Server.Url!.ToString(), RemoteConnectionKind.Control), Host.Token);
        var device = Assert.Single(await client.ScanAsync(Ct));
        await client.ConnectAsync(device, Ct);
        var pulse = new CoyotePulse(4, 500, 7);
        await client.SendAsync(device.Id, new(20, 10, 20, NativeProgram: new(new(NativeProgramKind.CoyoteV2Pulse, PulseA: pulse), 0, null)), Ct);
        Assert.Equal(pulse, host.Controller.Connections[host.Devices[0].Id].Output.NativeProgram!.Settings.PulseA);
        await client.DisconnectAsync(device.Id, Ct);
    }

    [Fact]
    public async Task CompiledPipelineUsesExistingDriverOverWebSocketGattAndConfirmsStop()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV3);
        await using var client = new DeviceController(new BluetoothDeviceTransport(new WebSocketGattAdapter(
            new(Guid.NewGuid(), "Pipeline loopback", host.Server.Url!.ToString()), Host.Token)));
        var device = Assert.Single(await client.ScanAsync(Ct));
        await client.ConnectAsync(device, Ct);
        var value = NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(.35));
        var output = NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode(device.Id));
        var graph = new PipelineDocument(Guid.NewGuid(), "loopback", [value, output],
            [new(new(value.Id, "value"), new(output.Id, "strength"))], []);
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, [], new Dictionary<string, DeviceDescriptor> { [device.Id] = device });
        await using var session = new PipelineSession(client);
        await session.StartAsync(plan);
        for (var i = 0; i < 100 && client.Connections[device.Id].Output.ChannelA == 0; i++)
            await Task.Delay(10, Ct);
        Assert.Equal(35, client.Connections[device.Id].Output.ChannelA);
        Assert.Equal(session.OwnerId, client.DeviceOwner(device.Id));
        await session.StopAsync();
        Assert.Equal(OutputFrame.Zero, client.Connections[device.Id].Output);
        Assert.Null(client.DeviceOwner(device.Id));
        await client.DisconnectAsync(device.Id, Ct);
    }

    [Theory]
    [InlineData(BluetoothProtocol.YokonexV1)]
    [InlineData(BluetoothProtocol.YokonexV2)]
    [InlineData(BluetoothProtocol.YokonexMotor)]
    [InlineData(BluetoothProtocol.YokonexPump)]
    [InlineData(BluetoothProtocol.CoyoteV2)]
    [InlineData(BluetoothProtocol.CoyoteV3)]
    [InlineData(BluetoothProtocol.Opossum)]
    [InlineData(BluetoothProtocol.Paw)]
    [InlineData(BluetoothProtocol.Civet)]
    public async Task ExistingDriversConnectThroughGattAndRestoreTheHost(BluetoothProtocol protocol)
    {
        await using var host = await Host.Create(protocol);
        await using var client = new DeviceController(new BluetoothDeviceTransport(new WebSocketGattAdapter(new(Guid.NewGuid(), "Test", host.Server.Url!.ToString()), Host.Token)));
        var remote = Assert.Single(await client.ScanAsync(Ct));
        Assert.Equal(DeviceOrigin.Remote, remote.Origin);
        Assert.False(remote.Simulated);
        Assert.Equal(protocol.ToString(), remote.ProtocolId);
        Assert.NotEqual(host.Devices[0].Id, remote.Id);
        await client.ConnectAsync(remote, Ct);
        Assert.True(client.Connections.ContainsKey(remote.Id));
        Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(host.Devices[0].Id));
        var writes = host.Adapter.Links[host.Devices[0].Id].Writes.Count;
        await client.SendAsync(remote.Id, OutputFrame.Zero, Ct);
        await client.DisconnectAsync(remote.Id, Ct);
        Assert.True(host.Adapter.Links[host.Devices[0].Id].Writes.Count >= writes);
        Assert.False(host.Adapter.Links[host.Devices[0].Id].Closed);
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Fact]
    public async Task AuthenticationAndLeaseTypesAreEnforced()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV3);
        await using var client = await Client.Create(host.Server.Url!, authenticate: false);
        var denied = await Assert.ThrowsAsync<RemoteRpcException>(() => client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        Assert.Equal("Error.RemoteAuthentication", denied.Key);
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Peer.CallAsync<bool>("system.authenticate", new { token = "wrong" }, Ct));
        await client.Authenticate();
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        var lease = await client.Acquire(device.Id, RemoteLeaseType.Gatt);
        Assert.Equal("\"gatt\"", JsonSerializer.Serialize(lease.Type, RemoteProtocol.Json));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Acquire(device.Id, RemoteLeaseType.Control));
        var mismatch = await Assert.ThrowsAsync<RemoteRpcException>(() => client.Execute<bool>(lease, "control.stop"));
        Assert.Equal("Error.RemoteMethod", mismatch.Key);
        await using var other = await Client.Create(host.Server.Url!);
        await Assert.ThrowsAsync<RemoteRpcException>(() => other.Execute<bool>(lease, "gatt.describe", new { deviceId = device.Id }));
        await client.Release(lease);
        var control = await other.Acquire(device.Id, RemoteLeaseType.Control);
        var wrongGatt = await Assert.ThrowsAsync<RemoteRpcException>(() => other.Execute<bool>(control, "gatt.describe", new { deviceId = device.Id }));
        Assert.Equal("Error.RemoteLeaseType", wrongGatt.Key);
    }

    [Fact]
    public async Task GattForwardsNotificationsAndRejectsUnknownAttributesAndExpiredWrites()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV3);
        await using var client = await Client.Create(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        var lease = await client.Acquire(device.Id, RemoteLeaseType.Gatt);
        var profile = BluetoothDeviceProfiles.FromId(device.ProtocolId)!;
        var description = await client.Execute<RemoteGattDescription>(lease, "gatt.describe", new
        {
            deviceId = device.Id
        });
        Assert.Equal(20, description.MaximumWriteLength);
        Assert.Contains(description.Attributes, attribute => attribute.Address == profile.Write && attribute.Write);
        var received = new TaskCompletionSource<RemoteGattNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Peer.Notification += (method, data) => { if (method == "gatt.notification") received.TrySetResult(RemoteProtocol.Read<RemoteGattNotification>(data)); };
        await client.Execute<bool>(lease, "gatt.subscribe", new
        {
            deviceId = device.Id,
            address = profile.Notify
        });
        host.Adapter.Links[host.Devices[0].Id].Notify([0xb1, 0, 2, 3]);
        Assert.Equal(new byte[] { 0xb1, 0, 2, 3 }, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct)).Notification.Data);
        var count = host.Adapter.Links[host.Devices[0].Id].Writes.Count;
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Execute<bool>(lease, "gatt.write", new { deviceId = device.Id, write = new GattWrite(new(Guid.NewGuid(), Guid.NewGuid()), [1]) }));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Execute<bool>(lease, "gatt.write", new { deviceId = device.Id, write = new GattWrite(profile.Write, new byte[21]) }));
        var stale = await Assert.ThrowsAsync<RemoteRpcException>(() => client.Execute<bool>(lease, "gatt.write", new { deviceId = device.Id, write = new GattWrite(profile.Write, [1]), deadline = -1 }));
        Assert.Equal("Error.RemoteExpired", stale.Key);
        Assert.Equal(count, host.Adapter.Links[host.Devices[0].Id].Writes.Count);
    }

    [Fact]
    public async Task ControlLimitsPauseSubscriptionsAndLeaseScopesAreIndependent()
    {
        await using var host = await Host.Create(BluetoothProtocol.YokonexV1, BluetoothProtocol.CoyoteV3);
        await using var client = await Client.Create(host.Server.Url!);
        var devices = await client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct);
        var first = await client.Acquire(devices[0].Id, RemoteLeaseType.Control);
        var second = await client.Acquire(devices[1].Id, RemoteLeaseType.Control);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Peer.Notification += (method, _) => { if (method == "control.status") changed.TrySetResult(); };
        await client.Execute<bool>(first, "control.subscribe", new
        {
            deviceId = devices[0].Id
        });
        await client.Execute<bool>(first, "control.output", new
        {
            frame = new OutputFrame(90, 80, 20, OutputLimit: 25)
        });
        await client.Execute<bool>(second, "control.output", new
        {
            frame = new OutputFrame(90, 80, 20)
        });
        Assert.Equal(25, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.Equal(40, host.Controller.Connections[host.Devices[1].Id].Output.ChannelA);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await client.Execute<bool>(first, "control.pause");
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.Equal(40, host.Controller.Connections[host.Devices[1].Id].Output.ChannelA);
        await client.Execute<bool>(first, "control.resume");
        await client.Execute<bool>(first, "control.strength", new
        {
            channelA = 80,
            channelB = 50
        });
        Assert.Equal(25, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        await client.Release(first);
        Assert.Equal(40, host.Controller.Connections[host.Devices[1].Id].Output.ChannelA);
        Assert.All(host.Devices, device => Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(device.Id)));
    }

    [Fact]
    public async Task HighLevelPlaybackUsesGeneralWaveformsAndDynamicChanges()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV3);
        await using var client = await Client.Create(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        var lease = await client.Acquire(device.Id, RemoteLeaseType.Control);
        var wave = new WaveformPreset(Guid.NewGuid(), "Constant", WaveShape.Custom, 1, 20, [1, 1]);
        var next = wave with
        {
            Id = Guid.NewGuid(),
            Name = "Next"
        };
        await client.Execute<bool>(lease, "control.playlist", new
        {
            playback = new RemoteControlProgram(new(Order: PlaybackOrder.Sequential, Playlist: [wave, next], OutputLimit: 30), wave, 70, 70, DurationSeconds: 10)
        });
        Assert.Equal(30, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.True(await client.Execute<bool>(lease, "control.waveform", new
        {
            waveform = next
        }));
        await client.Execute<bool>(lease, "control.command", new
        {
            deviceId = device.Id,
            command = (DeviceCommand)new QueryDevice(DeviceQuery.Battery)
        });
        var status = await client.Execute<RemoteStatus>(lease, "control.status", new
        {
            deviceId = device.Id
        });
        Assert.Equal(device.Id, status.DeviceId);
        await client.Execute<bool>(lease, "control.stop");
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Theory]
    [InlineData(RemoteLeaseType.Gatt)]
    [InlineData(RemoteLeaseType.Control)]
    public async Task ExpiryAndDisconnectZeroOutputWithoutReleasingServerOwnership(RemoteLeaseType type)
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV3);
        await using var client = await Client.Create(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        var lease = await client.Acquire(device.Id, type);
        if (type == RemoteLeaseType.Control)
            await client.Execute<bool>(lease, "control.output", new
            {
                frame = new OutputFrame(20, 30, 20)
            });
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Peer.Notification += (method, _) => { if (method == "lease.revoked") revoked.TrySetResult(); };
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(4), Ct);
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(host.Devices[0].Id));
        var next = await client.Acquire(device.Id, RemoteLeaseType.Control);
        await client.Execute<bool>(next, "control.output", new
        {
            frame = new OutputFrame(20, 30, 20)
        });
        client.Peer.Abort();
        await Wait(() => host.Controller.Connections[host.Devices[0].Id].Output.ChannelA == 0);
    }

    [Fact]
    public async Task ServerStopRevokesRawClientAndReleasesOwnership()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV3);
        await using var client = new DeviceController(new BluetoothDeviceTransport(new WebSocketGattAdapter(new(Guid.NewGuid(), "Test", host.Server.Url!.ToString()), Host.Token)));
        var remote = Assert.Single(await client.ScanAsync(Ct));
        await client.ConnectAsync(remote, Ct);
        await host.Server.DisposeAsync();
        await Wait(() => !client.Connections.ContainsKey(remote.Id));
        Assert.Null(host.Controller.DeviceOwner(host.Devices[0].Id));
    }

    [Fact]
    public async Task FragmentedJsonIsAcceptedButDuplicateRequestIdsNeverExecuteAgain()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV3);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.SubProtocol);
        await socket.ConnectAsync(host.Server.Url!, Ct);
        var packet = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            method = "system.authenticate",
            @params = new
            {
                token = Host.Token
            }
        }));
        await socket.SendAsync(packet.AsMemory(0, 10), WebSocketMessageType.Text, false, Ct);
        await socket.SendAsync(packet.AsMemory(10), WebSocketMessageType.Text, true, Ct);
        Assert.Contains("\"result\":true", await Receive(socket));
        await socket.SendAsync(packet, WebSocketMessageType.Text, true, Ct);
        Assert.Contains("Error.RemoteSequence", await Receive(socket));
    }

    private static async Task<string> Receive(ClientWebSocket socket)
    {
        var bytes = new byte[4096];
        var result = await socket.ReceiveAsync(bytes.AsMemory(), Ct);
        return Encoding.UTF8.GetString(bytes, 0, result.Count);
    }
    [Fact]
    public async Task LocalSimulationAndRemoteBluetoothCoexistWithPerDeviceInputGuards()
    {
        await using var host = await Host.Create(BluetoothProtocol.YokonexV1);
        await using var controller = new DeviceController(new SimulationTransport());
        await controller.AddSourceAsync("network", new BluetoothDeviceTransport(new WebSocketGattAdapter(new(Guid.NewGuid(), "Test", host.Server.Url!.ToString()), Host.Token)), Ct);
        var devices = await controller.ScanAsync(Ct);
        var local = devices.First(device => device.Origin == DeviceOrigin.Local && device.Kind == DeviceKind.DualChannel);
        var remote = Assert.Single(devices, device => device.Origin == DeviceOrigin.Remote);
        await controller.ConnectAsync(local, Ct);
        await controller.ConnectAsync(remote, Ct);
        Assert.True(controller.AreSimulated([local.Id]));
        Assert.False(controller.AreSimulated([remote.Id]));
        var simulatedOwner = Guid.NewGuid();
        var realOwner = Guid.NewGuid();
        await controller.AcquireTargetsAsync(simulatedOwner, [local.Id], Ct);
        await controller.AcquireTargetsAsync(realOwner, [remote.Id], Ct);
        await controller.SetSimulatedMotionAsync(simulatedOwner, 45);
        await Assert.ThrowsAsync<LocalizedArgumentException>(() => controller.SetSimulatedMotionAsync(realOwner, 45));
        Assert.Equal(DeviceOrigin.Remote, remote.Origin);
        await controller.StopOwnedAsync(realOwner, Ct);
        await controller.RemoveSourceAsync("network", Ct);
        Assert.True(controller.Connections.ContainsKey(local.Id));
        Assert.Equal(simulatedOwner, controller.DeviceOwner(local.Id));
        await controller.StopOwnedAsync(simulatedOwner, Ct);
    }

    [Fact]
    public async Task TimedServerClosesConnectionsAndSharedListenersKeepOtherModesRunning()
    {
        await using var host = await Host.Create(BluetoothProtocol.YokonexV1, BluetoothProtocol.CoyoteV3);
        await host.Server.DisposeAsync();
        var port = host.Server.Url!.Port;
        await using var first = new RemoteServer(new(Guid.NewGuid(), host.Controller, [host.Devices[0].Id], new(Port: port), TimeSpan.FromSeconds(1)), Host.Token);
        await using var second = new RemoteServer(new(Guid.NewGuid(), host.Controller, [host.Devices[1].Id], new(Port: port)), Host.Token);
        await first.StartAsync(ct: Ct);
        await second.StartAsync(ct: Ct);
        await using var firstClient = await Client.Create(first.Url!);
        await using var secondClient = await Client.Create(second.Url!);
        var firstDevice = Assert.Single(await firstClient.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        var secondDevice = Assert.Single(await secondClient.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        await firstClient.Acquire(firstDevice.Id, RemoteLeaseType.Gatt);
        var lease = await secondClient.Acquire(secondDevice.Id, RemoteLeaseType.Control);
        await firstClient.Peer.Completion.WaitAsync(TimeSpan.FromSeconds(4), Ct);
        Assert.Null(host.Controller.DeviceOwner(host.Devices[0].Id));
        Assert.True(second.Session.IsRunning);
        await secondClient.Execute<bool>(lease, "control.output", new
        {
            frame = new OutputFrame(15, 10, 20)
        });
        Assert.Equal(15, host.Controller.Connections[host.Devices[1].Id].Output.ChannelA);
    }

    [Fact]
    public async Task NativeProgramsPumpsAndSensorCommandsUseHighLevelApi()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV2, BluetoothProtocol.YokonexPump, BluetoothProtocol.Civet);
        await using var client = await Client.Create(host.Server.Url!);
        var devices = await client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct);
        var electrical = devices.Single(device => device.ProtocolId == BluetoothProtocol.CoyoteV2.ToString());
        var pump = devices.Single(device => device.Kind == DeviceKind.Pump);
        var sensor = devices.Single(device => device.Kind == DeviceKind.Sensor);
        var electricalLease = await client.Acquire(electrical.Id, RemoteLeaseType.Control);
        var pumpLease = await client.Acquire(pump.Id, RemoteLeaseType.Control);
        var sensorLease = await client.Acquire(sensor.Id, RemoteLeaseType.Control);
        await client.Execute<bool>(electricalLease, "control.native", new
        {
            playback = new RemoteControlProgram(new(Kind: RemotePlaybackKind.NativeProgram, NativeProgram: new(NativeProgramKind.CoyoteV2Pulse)), ChannelA: 20)
        });
        await client.Execute<bool>(pumpLease, "control.pump", new
        {
            playback = new RemoteControlProgram(new(Kind: RemotePlaybackKind.Pump, Pump: new(PumpDirection.Forward, true)))
        });
        await client.Execute<bool>(sensorLease, "control.command", new
        {
            deviceId = sensor.Id,
            command = (DeviceCommand)new ConfigurePressureSensor(true, SensorOrientation.Flipped)
        });
        var pumpOutput = host.Controller.Connections[host.Devices.Single(device => device.Kind == DeviceKind.Pump).Id].Output.Pump;
        Assert.Equal(PumpDirection.Forward, pumpOutput!.Direction);
        Assert.True(pumpOutput.Water);
        Assert.Equal(new byte[] { 0x66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 2 }, host.Adapter.Links[host.Devices.Single(device => device.Kind == DeviceKind.Sensor).Id].Writes[^1].Data);
    }

    [Fact]
    public void AllTypedCommandsAndPawTriggersRoundTripThroughTheWireFormat()
    {
        DeviceCommand[] commands = [new QueryDevice(), new ConfigureMotionStream(true), new ControlStepCounter(StepCounterAction.Reset),
            new ConfigureSensorStream(true), new ConfigureIndicator(2), new ConfigureShoulderLight(2, 3), new CalibratePawAngles(),
            new ResetPawParameters(), new ConfigurePressureSensor(true), new SynchronizeDisplay(), new ConfigureLight(false)];
        PawTrigger[] triggers = [new PawNoTrigger(), new PawLiveData(), new PawRandomReaction(), new PawProbability([new(1, 100)]), new PawVoltage(), new PawButtonMotion()];
        foreach (var command in commands.Concat(triggers.Select(trigger => (DeviceCommand)new ConfigurePaw(trigger))))
        {
            var json = RemoteProtocol.Element(command);
            var restored = RemoteProtocol.Read<DeviceCommand>(json);
            Assert.Equal(command.GetType(), restored.GetType());
            restored.Validate();
            Assert.Equal(json.GetRawText(), RemoteProtocol.Element(restored).GetRawText());
        }
    }

    [Fact]
    public async Task RawClientRenewsItsLeaseAndPreservesTheRealWriteLength()
    {
        await using var host = await Host.CreateWithMtu(24, BluetoothProtocol.Opossum);
        await using var adapter = new WebSocketGattAdapter(new(Guid.NewGuid(), "Test", host.Server.Url!.ToString()), Host.Token);
        var peripheral = Assert.Single(await adapter.ScanAsync(Ct));
        await using var link = await adapter.ConnectAsync(peripheral, Ct);
        Assert.Equal(24, link.MaximumWriteLength);
        await Task.Delay(1400, Ct);
        var profile = BluetoothDeviceProfiles.FromId(BluetoothProtocol.Opossum.ToString())!;
        await link.WriteAsync(new GattWrite(profile.Write, new byte[24], GattWriteMode.WithResponse), Ct);
        Assert.Equal(24, host.Adapter.Links[host.Devices[0].Id].Writes[^1].Data.Length);
        await Assert.ThrowsAsync<RemoteRpcException>(() => link.WriteAsync(new GattWrite(profile.Write, new byte[25]), Ct));
        Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(host.Devices[0].Id));
    }

    [Fact]
    public async Task FailedOutputRetainsAnUnconfirmedLeaseAndDisconnectRetriesZero()
    {
        await using var host = await Host.Create(BluetoothProtocol.CoyoteV3);
        await using var client = await Client.Create(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        var lease = await client.Acquire(device.Id, RemoteLeaseType.Control);
        host.Adapter.Links[host.Devices[0].Id].FailWrites = true;
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Execute<bool>(lease, "control.output", new { frame = new OutputFrame(20, 30, 20) }));
        Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(host.Devices[0].Id));
        host.Adapter.Links[host.Devices[0].Id].FailWrites = false;
        client.Peer.Abort();
        await Task.Delay(100, Ct);
        await using var next = await Client.Create(host.Server.Url!);
        var recovered = await next.Acquire(device.Id, RemoteLeaseType.Control);
        await next.Execute<bool>(recovered, "control.output", new
        {
            frame = new OutputFrame(10, 15, 20)
        });
        await next.Release(recovered);
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Fact]
    public async Task LeaseExpiryCancelsAutonomousPawTriggers()
    {
        await using var host = await Host.Create(BluetoothProtocol.Paw);
        await using var client = await Client.Create(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", ct: Ct));
        var lease = await client.Acquire(device.Id, RemoteLeaseType.Control);
        await client.Execute<bool>(lease, "control.command", new
        {
            deviceId = device.Id,
            command = (DeviceCommand)new ConfigurePaw(new PawRandomReaction())
        });
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Peer.Notification += (method, _) => { if (method == "lease.revoked") revoked.TrySetResult(); };
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(4), Ct);
        var last = host.Adapter.Links[host.Devices[0].Id].Writes[^1].Data;
        Assert.Equal((byte)0x50, last[0]);
        Assert.Equal((byte)0, last[2]);
    }
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class Host : IAsyncDisposable
    {
        public const string Token = "test-only-websocket-token-123456789";
        public required HardwareGattAdapter Adapter { get; init; }
        public required DeviceController Controller { get; init; }
        public required DeviceDescriptor[] Devices { get; init; }
        public required RemoteServer Server { get; init; }
        public static Task<Host> Create(params BluetoothProtocol[] protocols) => CreateWithMtu(20, protocols);
        public static async Task<Host> CreateWithMtu(int maximumWriteLength, params BluetoothProtocol[] protocols)
        {
            var adapter = new HardwareGattAdapter(protocols) { MaximumWriteLength = maximumWriteLength };
            var controller = new DeviceController(new BluetoothDeviceTransport(adapter));
            var devices = (await controller.ScanAsync(Ct)).ToArray();
            foreach (var device in devices)
                await controller.ConnectAsync(device, Ct);
            var server = new RemoteServer(new(Guid.NewGuid(), controller, devices.Select(device => device.Id).ToArray(), new(Port: 0, LeaseMilliseconds: 1000, ControlLimit: 40)), Token);
            try
            {
                await server.StartAsync(ct: Ct);
            }
            catch { await controller.DisposeAsync(); throw; }
            return new()
            {
                Adapter = adapter,
                Controller = controller,
                Devices = devices,
                Server = server
            };
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Server.DisposeAsync();
            }
            finally { await Controller.DisposeAsync(); }
        }
    }
    private sealed class Client : IAsyncDisposable
    {
        public required WebSocketRpcPeer Peer { get; init; }
        private RemoteHello? _hello;
        private long _helloAt;
        private readonly Dictionary<Guid, long> _sequences = [];
        public static async Task<Client> Create(Uri uri, bool authenticate = true)
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(RemoteProtocol.SubProtocol);
            await socket.ConnectAsync(uri, Ct);
            var client = new Client { Peer = new(socket) };
            client.Peer.Start();
            if (authenticate)
                await client.Authenticate();
            return client;
        }
        public async Task Authenticate()
        {
            await Peer.CallAsync<bool>("system.authenticate", new
            {
                token = Host.Token
            }, Ct);
            _hello = await Peer.CallAsync<RemoteHello>("system.hello", ct: Ct);
            _helloAt = Stopwatch.GetTimestamp();
        }
        public Task<RemoteLease> Acquire(string deviceId, RemoteLeaseType type) => Peer.CallAsync<RemoteLease>("lease.acquire", new { deviceIds = new[] { deviceId }, type }, Ct);
        public Task<bool> Release(RemoteLease lease) => Peer.CallAsync<bool>("lease.release", new { lease = lease.Id }, Ct);
        public Task<T> Execute<T>(RemoteLease lease, string method, object? args = null)
        {
            var parameters = RemoteProtocol.Element(args ?? new
            {
            }).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
            parameters["lease"] = lease.Id;
            parameters["sequence"] = _sequences[lease.Id] = _sequences.GetValueOrDefault(lease.Id) + 1;
            parameters.TryAdd("deadline", _hello!.ClockMilliseconds + (long)Stopwatch.GetElapsedTime(_helloAt).TotalMilliseconds + 500);
            return Peer.CallAsync<T>(method, parameters, Ct);
        }
        public ValueTask DisposeAsync() => Peer.DisposeAsync();
    }
}
