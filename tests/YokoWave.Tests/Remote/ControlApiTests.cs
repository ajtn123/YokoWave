using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using YokoWave.Remote;
using YokoWave.Tests.Support.Bluetooth;
using YokoWave.Tests.Support.Remote;

namespace YokoWave.Tests.Remote;

public sealed class ControlApiTests
{
    [Theory]
    [InlineData("play")]
    [InlineData("output")]
    [InlineData("duration")]
    public async Task InvalidControlRequestsDoNotChangeHardwareSettingsOrStopExistingOutput(string operation)
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Control);
        await client.ExecuteAsync<bool>(lease, "control.output", new { frame = new OutputFrame(20, 15, 20) });
        var oldSettings = host.Controller.GetOutputSettings(host.Devices[0].Id);
        object parameters = operation switch
        {
            "play" => new
            {
                playback = new RemoteControlProgram(new(Gentle: true), ChannelA: -1)
            },
            "duration" => new
            {
                playback = new RemoteControlProgram(new(Gentle: true), ChannelA: 20, DurationSeconds: 0)
            },
            _ => new
            {
                frame = new OutputFrame(20, 15, 20, WindowA: [new(1, 0)], Gentle: true)
            }
        };
        string method = operation == "output" ? "control.output" : "control.play";
        var failure = await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, method, parameters));
        Assert.Equal(-32602, failure.Code);
        Assert.Equal(oldSettings, host.Controller.GetOutputSettings(host.Devices[0].Id));
        Assert.Equal(20, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        await client.ExecuteAsync<bool>(lease, "control.output", new { frame = new OutputFrame(10, 5, 20) });
        Assert.Equal(10, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Fact]
    public async Task InvalidNativeMotorOutputRespectsServerCapBeforeReplacingPlayback()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.YokonexMotor);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Control);
        await client.ExecuteAsync<bool>(lease, "control.output", new { frame = new OutputFrame(20, 15, 20) });
        var motor = new NativeProgramSettings(NativeProgramKind.MotorPreset);
        var outputFailure = await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "control.output", new { frame = new OutputFrame(100, 100, 20, NativeProgram: new(motor, 0, null)) }));
        Assert.Equal(-32602, outputFailure.Code);
        Assert.Equal("Error.NativeMotorLimit", outputFailure.Key);
        var programFailure = await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "control.native", new { playback = new RemoteControlProgram(new(Kind: RemotePlaybackKind.NativeProgram, NativeProgram: motor), ChannelA: 100, ChannelB: 100) }));
        Assert.Equal(-32602, programFailure.Code);
        Assert.Equal("Error.NativeMotorLimit", programFailure.Key);
        Assert.Equal(20, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        await client.ExecuteAsync<bool>(lease, "control.output", new { frame = new OutputFrame(10, 5, 20) });
        Assert.Equal(10, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Fact]
    public async Task InvalidLivePlaylistWaveformIsRejectedBeforeChangingAnyRunningNode()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Control);
        var wave = new WaveformAsset(Guid.NewGuid(), "wave", WaveShape.Custom, 1, 20, [1, 1]);
        await client.ExecuteAsync<bool>(lease, "control.playlist", new { playback = new RemoteControlProgram(new(Order: PlaybackOrder.Sequential, Playlist: [wave]), wave, 20, 15) });
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "control.waveform", new { waveform = wave with { Id = Guid.NewGuid() } }));
        await client.ExecuteAsync<bool>(lease, "control.strength", new { channelA = 10, channelB = 5 });
        Assert.Equal(10, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Theory]
    [InlineData(BluetoothProtocol.Civet)]
    [InlineData(BluetoothProtocol.YokonexV1)]
    public async Task ControlStatusRefreshDoesNotMakeOldSensorInputFreshAgain(BluetoothProtocol protocol)
    {
        await using var host = await RemoteTestHost.CreateAsync(protocol);
        await using var transport = new WebSocketControlTransport(new(Guid.NewGuid(), "sensor", host.Server.Url!.ToString(), RemoteConnectionKind.Control), RemoteTestHost.Token);
        await using var controller = new DeviceController(transport);
        var device = Assert.Single(await controller.ScanAsync(Ct));
        await controller.ConnectAsync(device, Ct);
        var link = host.Adapter.Links[host.Devices[0].Id];
        if (protocol == BluetoothProtocol.Civet)
        {
            link.Notify([0xd0, 0, 0, 0, 0, 0, 0, 0, 100, 0]);
            await AsyncAssert.EventuallyAsync(() => controller.Connections[device.Id].Telemetry?.PressureKpa == 1, TimeSpan.FromSeconds(4));
        }
        else
        {
            link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 6, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]));
            await AsyncAssert.EventuallyAsync(() => controller.Connections[device.Id].Motion is not null, TimeSpan.FromSeconds(4));
        }

        await Task.Delay(2200, Ct);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += (_, _) => refreshed.TrySetResult();
        await transport.RefreshFeaturesAsync(device.Id, Ct);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        var captured = protocol == BluetoothProtocol.Civet ? controller.Connections[device.Id].TelemetryCapturedAt : controller.Connections[device.Id].Motion?.CapturedAt;
        Assert.True(controller.Clock.GetUtcNow() - captured >= TimeSpan.FromSeconds(2));
        if (protocol == BluetoothProtocol.Civet)
        {
            link.Notify([0xd0, 0, 0, 0, 0, 0, 0, 0, 200, 0]);
            await AsyncAssert.EventuallyAsync(() => controller.Connections[device.Id].Telemetry?.PressureKpa == 2, TimeSpan.FromSeconds(4));
            Assert.True(controller.Clock.GetUtcNow() - controller.Connections[device.Id].TelemetryCapturedAt < TimeSpan.FromSeconds(2));
        }
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
    public async Task HighLevelClientPreservesDeviceIdentityStatusAndConfirmedStop(BluetoothProtocol protocol)
    {
        await using var host = await RemoteTestHost.CreateAsync(protocol);
        await using var transport = new WebSocketControlTransport(new(Guid.NewGuid(), "control", host.Server.Url!.ToString(), RemoteConnectionKind.Control), RemoteTestHost.Token);
        var device = Assert.Single(await transport.ScanAsync(Ct));
        Assert.Equal(protocol.ToString(), device.ProtocolId);
        Assert.Equal(DeviceOrigin.Remote, device.Origin);
        await transport.ConnectAsync(device, Ct);
        if (!device.IsInput)
        {
            var frame = device.Kind == DeviceKind.Pump ? OutputFrame.Zero with
            {
                Pump = new(PumpDirection.Reverse, true)
            }

            : new OutputFrame(30, 20, 25, ChannelC: 10);
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
            Nodes = graph.Nodes.Select(n => n.Id == serverNode.Id ? NodeDefinition.Create(n.Type, new ControlServerNode(device.Id, new(Port: 0, LeaseMilliseconds: 1000)), n.Id) : n).ToArray()
        };
        var services = new PipelineServices();
        services.Credentials[serverNode.Id] = new(RemoteTestHost.Token);
        await using var session = new PipelineSession(controller, services);
        await session.StartAsync(new PipelineCompiler(RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry())).Compile(graph, [], new Dictionary<string, DeviceDescriptor> { [device.Id] = device }));
        var endpoint = services.Endpoints[serverNode.Id];
        await using var client = new WebSocketControlTransport(new(Guid.NewGuid(), "client", endpoint.Url, RemoteConnectionKind.Control), RemoteTestHost.Token);
        var remote = Assert.Single(await client.ScanAsync(Ct));
        await client.ConnectAsync(remote, Ct);
        await client.SendAsync(remote.Id, new(80, 70, 20), Ct);
        await AsyncAssert.EventuallyAsync(() => controller.Connections[device.Id].Output.ChannelA == 25, TimeSpan.FromSeconds(4));
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
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV2);
        await using var client = new WebSocketControlTransport(new(Guid.NewGuid(), "native", host.Server.Url!.ToString(), RemoteConnectionKind.Control), RemoteTestHost.Token);
        var device = Assert.Single(await client.ScanAsync(Ct));
        await client.ConnectAsync(device, Ct);
        var pulse = new CoyotePulse(4, 500, 7);
        await client.SendAsync(device.Id, new(20, 10, 20, NativeProgram: new(new(NativeProgramKind.CoyoteV2Pulse, PulseA: pulse), 0, null)), Ct);
        Assert.Equal(pulse, host.Controller.Connections[host.Devices[0].Id].Output.NativeProgram!.Settings.PulseA);
        await client.DisconnectAsync(device.Id, Ct);
    }

    [Fact]
    public async Task ControlLimitsPauseSubscriptionsAndLeaseScopesAreIndependent()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.YokonexV1, BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var devices = await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct);
        var first = await client.AcquireAsync(devices[0].Id, RemoteLeaseType.Control);
        var second = await client.AcquireAsync(devices[1].Id, RemoteLeaseType.Control);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Peer.Notification += (method, _) =>
        {
            if (method == "control.status")
                changed.TrySetResult();
        };
        await client.ExecuteAsync<bool>(first, "control.subscribe", new { deviceId = devices[0].Id });
        await client.ExecuteAsync<bool>(first, "control.output", new { frame = new OutputFrame(90, 80, 20, OutputLimit: 25) });
        await client.ExecuteAsync<bool>(second, "control.output", new { frame = new OutputFrame(90, 80, 20) });
        Assert.Equal(25, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.Equal(40, host.Controller.Connections[host.Devices[1].Id].Output.ChannelA);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await client.ExecuteAsync<bool>(first, "control.pause");
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.Equal(40, host.Controller.Connections[host.Devices[1].Id].Output.ChannelA);
        await client.ExecuteAsync<bool>(first, "control.resume");
        await client.ExecuteAsync<bool>(first, "control.strength", new { channelA = 80, channelB = 50 });
        Assert.Equal(25, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        await client.ReleaseAsync(first);
        Assert.Equal(40, host.Controller.Connections[host.Devices[1].Id].Output.ChannelA);
        Assert.All(host.Devices, device => Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(device.Id)));
    }

    [Fact]
    public async Task HighLevelPlaybackUsesGeneralWaveformsAndDynamicChanges()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Control);
        var wave = new WaveformAsset(Guid.NewGuid(), "Constant", WaveShape.Custom, 1, 20, [1, 1]);
        var next = wave with
        {
            Id = Guid.NewGuid(),
            Name = "Next"
        };
        await client.ExecuteAsync<bool>(lease, "control.playlist", new { playback = new RemoteControlProgram(new(Order: PlaybackOrder.Sequential, Playlist: [wave, next], OutputLimit: 30), wave, 70, 70, DurationSeconds: 10) });
        Assert.Equal(30, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.True(await client.ExecuteAsync<bool>(lease, "control.waveform", new { waveform = next }));
        await client.ExecuteAsync<bool>(lease, "control.command", new { deviceId = device.Id, command = (DeviceCommand)new QueryDevice(DeviceQuery.Battery) });
        var status = await client.ExecuteAsync<RemoteStatus>(lease, "control.status", new { deviceId = device.Id });
        Assert.Equal(device.Id, status.DeviceId);
        await client.ExecuteAsync<bool>(lease, "control.stop");
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Fact]
    public async Task NativeProgramsPumpsAndSensorCommandsUseHighLevelApi()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV2, BluetoothProtocol.YokonexPump, BluetoothProtocol.Civet);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var devices = await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct);
        var electrical = devices.Single(device => device.ProtocolId == BluetoothProtocol.CoyoteV2.ToString());
        var pump = devices.Single(device => device.Kind == DeviceKind.Pump);
        var sensor = devices.Single(device => device.Kind == DeviceKind.Sensor);
        var electricalLease = await client.AcquireAsync(electrical.Id, RemoteLeaseType.Control);
        var pumpLease = await client.AcquireAsync(pump.Id, RemoteLeaseType.Control);
        var sensorLease = await client.AcquireAsync(sensor.Id, RemoteLeaseType.Control);
        await client.ExecuteAsync<bool>(electricalLease, "control.native", new { playback = new RemoteControlProgram(new(Kind: RemotePlaybackKind.NativeProgram, NativeProgram: new(NativeProgramKind.CoyoteV2Pulse)), ChannelA: 20) });
        await client.ExecuteAsync<bool>(pumpLease, "control.pump", new { playback = new RemoteControlProgram(new(Kind: RemotePlaybackKind.Pump, Pump: new(PumpDirection.Forward, true))) });
        await client.ExecuteAsync<bool>(sensorLease, "control.command", new { deviceId = sensor.Id, command = (DeviceCommand)new ConfigurePressureSensor(true, SensorOrientation.Flipped) });
        var pumpOutput = host.Controller.Connections[host.Devices.Single(device => device.Kind == DeviceKind.Pump).Id].Output.Pump;
        Assert.Equal(PumpDirection.Forward, pumpOutput!.Direction);
        Assert.True(pumpOutput.Water);
        Assert.Equal(new byte[] { 0x66, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0, 2 }, host.Adapter.Links[host.Devices.Single(device => device.Kind == DeviceKind.Sensor).Id].Writes[^1].Data);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
