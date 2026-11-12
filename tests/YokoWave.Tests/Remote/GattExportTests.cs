using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using YokoWave.Tests.Support.Remote;

namespace YokoWave.Tests.Remote;

public sealed class GattExportTests
{
    [Fact]
    public async Task ConnectedGattDeviceCanBeExportedAfterItDisappearsFromScanResults()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        host.Adapter.Peripherals.Clear();
        Assert.Empty(await host.Controller.ScanAsync(Ct));
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Gatt);
        var description = await client.ExecuteAsync<RemoteGattDescription>(lease, "gatt.describe", new { deviceId = device.Id });
        Assert.Equal(host.Devices[0].Name, description.Device.Name);
        await client.ReleaseAsync(lease);
        Assert.False(host.Adapter.Links[host.Devices[0].Id].Closed);
    }

    [Fact]
    public async Task CompiledPipelineUsesExistingDriverOverWebSocketGattAndConfirmsStop()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = new DeviceController(new BluetoothDeviceTransport(new WebSocketGattAdapter(new(Guid.NewGuid(), "Pipeline loopback", host.Server.Url!.ToString()), RemoteTestHost.Token)));
        var device = Assert.Single(await client.ScanAsync(Ct));
        await client.ConnectAsync(device, Ct);
        var value = NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(.35));
        var output = NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode(device.Id));
        var graph = new PipelineDocument(Guid.NewGuid(), "loopback", [value, output], [new(new(value.Id, "value"), new(output.Id, "strength"))], []);
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph, [], new Dictionary<string, DeviceDescriptor> { [device.Id] = device });
        await using var session = new PipelineSession(client);
        await session.StartAsync(plan);
        await AsyncAssert.EventuallyAsync(() => client.Connections[device.Id].Output.ChannelA != 0);
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
        await using var host = await RemoteTestHost.CreateAsync(protocol);
        await using var client = new DeviceController(new BluetoothDeviceTransport(new WebSocketGattAdapter(new(Guid.NewGuid(), "Test", host.Server.Url!.ToString()), RemoteTestHost.Token)));
        var remote = Assert.Single(await client.ScanAsync(Ct));
        Assert.Equal(DeviceOrigin.Remote, remote.Origin);
        Assert.False(remote.Simulated);
        Assert.Equal(protocol.ToString(), remote.ProtocolId);
        Assert.NotEqual(host.Devices[0].Id, remote.Id);
        await client.ConnectAsync(remote, Ct);
        Assert.True(client.Connections.ContainsKey(remote.Id));
        Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(host.Devices[0].Id));
        int writes = host.Adapter.Links[host.Devices[0].Id].Writes.Count;
        await client.SendAsync(remote.Id, OutputFrame.Zero, Ct);
        await client.DisconnectAsync(remote.Id, Ct);
        Assert.True(host.Adapter.Links[host.Devices[0].Id].Writes.Count >= writes);
        Assert.False(host.Adapter.Links[host.Devices[0].Id].Closed);
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Fact]
    public async Task GattForwardsNotificationsAndRejectsUnknownAttributesAndExpiredWrites()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Gatt);
        var profile = BluetoothDeviceProfiles.FromId(device.ProtocolId)!;
        var description = await client.ExecuteAsync<RemoteGattDescription>(lease, "gatt.describe", new { deviceId = device.Id });
        Assert.Equal(20, description.MaximumWriteLength);
        Assert.Contains(description.Attributes, attribute => attribute.Address == profile.Write && attribute.Write);
        var received = new TaskCompletionSource<RemoteGattNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Peer.Notification += (method, data) =>
        {
            if (method == "gatt.notification")
                received.TrySetResult(RemoteProtocol.Read<RemoteGattNotification>(data));
        };
        await client.ExecuteAsync<bool>(lease, "gatt.subscribe", new { deviceId = device.Id, address = profile.Notify });
        host.Adapter.Links[host.Devices[0].Id].Notify([0xb1, 0, 2, 3]);
        Assert.Equal(new byte[] { 0xb1, 0, 2, 3 }, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct)).Notification.Data);
        int count = host.Adapter.Links[host.Devices[0].Id].Writes.Count;
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "gatt.write", new { deviceId = device.Id, write = new GattWrite(new(Guid.NewGuid(), Guid.NewGuid()), [1]) }));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "gatt.write", new { deviceId = device.Id, write = new GattWrite(profile.Write, new byte[21]) }));
        var invalidBatch = await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "gatt.batch", new { deviceId = device.Id, writes = new GattWrite[] { new(profile.Write, [1]), null! } }));
        Assert.Equal(-32602, invalidBatch.Code);
        var stale = await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "gatt.write", new { deviceId = device.Id, write = new GattWrite(profile.Write, [1]), deadline = -1 }));
        Assert.Equal("Error.RemoteExpired", stale.Key);
        Assert.Equal(count, host.Adapter.Links[host.Devices[0].Id].Writes.Count);
    }

    [Fact]
    public async Task RawClientRenewsItsLeaseAndPreservesTheRealWriteLength()
    {
        await using var host = await RemoteTestHost.CreateWithMtuAsync(24, BluetoothProtocol.Opossum);
        await using var adapter = new WebSocketGattAdapter(new(Guid.NewGuid(), "Test", host.Server.Url!.ToString()), RemoteTestHost.Token);
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
