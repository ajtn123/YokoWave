using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Remote;
using YokoWave.Tests.Support.Remote;

namespace YokoWave.Tests.Remote;

public sealed class LeaseLifecycleTests
{
    [Fact]
    public async Task AuthenticationAndLeaseTypesAreEnforced()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!, authenticate: false);
        var denied = await Assert.ThrowsAsync<RemoteRpcException>(() => client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        Assert.Equal("Error.RemoteAuthentication", denied.Key);
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Peer.CallAsync<bool>("system.authenticate", new { token = "wrong" }, Ct));
        await client.AuthenticateAsync();
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Gatt);
        Assert.Equal("\"gatt\"", JsonSerializer.Serialize(lease.Type, RemoteProtocol.Json));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.AcquireAsync(device.Id, RemoteLeaseType.Control));
        var mismatch = await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "control.stop"));
        Assert.Equal("Error.RemoteMethod", mismatch.Key);
        await using var other = await RemoteTestClient.CreateAsync(host.Server.Url!);
        await Assert.ThrowsAsync<RemoteRpcException>(() => other.ExecuteAsync<bool>(lease, "gatt.describe", new { deviceId = device.Id }));
        await client.ReleaseAsync(lease);
        var control = await other.AcquireAsync(device.Id, RemoteLeaseType.Control);
        var wrongGatt = await Assert.ThrowsAsync<RemoteRpcException>(() => other.ExecuteAsync<bool>(control, "gatt.describe", new { deviceId = device.Id }));
        Assert.Equal("Error.RemoteLeaseType", wrongGatt.Key);
    }

    [Theory]
    [InlineData(RemoteLeaseType.Gatt)]
    [InlineData(RemoteLeaseType.Control)]
    public async Task ExpiryAndDisconnectZeroOutputWithoutReleasingServerOwnership(RemoteLeaseType type)
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, type);
        if (type == RemoteLeaseType.Control)
        {
            await client.ExecuteAsync<bool>(lease, "control.output", new { frame = new OutputFrame(20, 30, 20) });
        }

        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Peer.Notification += (method, _) =>
        {
            if (method == "lease.revoked")
                revoked.TrySetResult();
        };
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(4), Ct);
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
        Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(host.Devices[0].Id));
        var next = await client.AcquireAsync(device.Id, RemoteLeaseType.Control);
        await client.ExecuteAsync<bool>(next, "control.output", new { frame = new OutputFrame(20, 30, 20) });
        client.Peer.Abort();
        await AsyncAssert.EventuallyAsync(() => host.Controller.Connections[host.Devices[0].Id].Output.ChannelA == 0, TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task ServerStopRevokesRawClientAndReleasesOwnership()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = new DeviceController(new BluetoothDeviceTransport(new WebSocketGattAdapter(new(Guid.NewGuid(), "Test", host.Server.Url!.ToString()), RemoteTestHost.Token)));
        var remote = Assert.Single(await client.ScanAsync(Ct));
        await client.ConnectAsync(remote, Ct);
        await host.Server.DisposeAsync();
        await AsyncAssert.EventuallyAsync(() => !client.Connections.ContainsKey(remote.Id), TimeSpan.FromSeconds(4));
        Assert.Null(host.Controller.DeviceOwner(host.Devices[0].Id));
    }

    [Fact]
    public async Task LocalSimulationAndRemoteBluetoothCoexistWithPerDeviceInputGuards()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.YokonexV1);
        await using var controller = new DeviceController(new SimulationTransport());
        await controller.AddSourceAsync("network", new BluetoothDeviceTransport(new WebSocketGattAdapter(new(Guid.NewGuid(), "Test", host.Server.Url!.ToString()), RemoteTestHost.Token)), Ct);
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
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.YokonexV1, BluetoothProtocol.CoyoteV3);
        await host.Server.DisposeAsync();
        int port = host.Server.Url!.Port;
        await using var first = new RemoteServer(new(Guid.NewGuid(), host.Controller, [host.Devices[0].Id], new(Port: port), TimeSpan.FromSeconds(1)), RemoteTestHost.Token);
        await using var second = new RemoteServer(new(Guid.NewGuid(), host.Controller, [host.Devices[1].Id], new(Port: port)), RemoteTestHost.Token);
        await first.StartAsync(cancellationToken: Ct);
        await second.StartAsync(cancellationToken: Ct);
        await using var firstClient = await RemoteTestClient.CreateAsync(first.Url!);
        await using var secondClient = await RemoteTestClient.CreateAsync(second.Url!);
        var firstDevice = Assert.Single(await firstClient.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var secondDevice = Assert.Single(await secondClient.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        await firstClient.AcquireAsync(firstDevice.Id, RemoteLeaseType.Gatt);
        var lease = await secondClient.AcquireAsync(secondDevice.Id, RemoteLeaseType.Control);
        await firstClient.Peer.Completion.WaitAsync(TimeSpan.FromSeconds(4), Ct);
        Assert.Null(host.Controller.DeviceOwner(host.Devices[0].Id));
        Assert.True(second.Session.IsRunning);
        await secondClient.ExecuteAsync<bool>(lease, "control.output", new { frame = new OutputFrame(15, 10, 20) });
        Assert.Equal(15, host.Controller.Connections[host.Devices[1].Id].Output.ChannelA);
    }

    [Fact]
    public async Task FailedOutputRetainsAnUnconfirmedLeaseAndDisconnectRetriesZero()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Control);
        host.Adapter.Links[host.Devices[0].Id].FailWrites = true;
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "control.output", new { frame = new OutputFrame(20, 30, 20) }));
        Assert.Equal(host.Server.Session.Id, host.Controller.DeviceOwner(host.Devices[0].Id));
        var link = host.Adapter.Links[host.Devices[0].Id];
        link.FailWrites = false;
        int writesBeforeDisconnect = link.Writes.Count;
        client.Peer.Abort();
        await AsyncAssert.EventuallyAsync(() => link.Writes.Count > writesBeforeDisconnect, TimeSpan.FromSeconds(4));
        await using var next = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var recovered = await next.AcquireAsync(device.Id, RemoteLeaseType.Control);
        await next.ExecuteAsync<bool>(recovered, "control.output", new { frame = new OutputFrame(10, 15, 20) });
        await next.ReleaseAsync(recovered);
        Assert.Equal(0, host.Controller.Connections[host.Devices[0].Id].Output.ChannelA);
    }

    [Fact]
    public async Task LeaseExpiryCancelsAutonomousPawTriggers()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.Paw);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        var lease = await client.AcquireAsync(device.Id, RemoteLeaseType.Control);
        await client.ExecuteAsync<bool>(lease, "control.command", new { deviceId = device.Id, command = (DeviceCommand)new ConfigurePaw(new PawRandomReaction()) });
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Peer.Notification += (method, _) =>
        {
            if (method == "lease.revoked")
                revoked.TrySetResult();
        };
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(4), Ct);
        byte[] last = host.Adapter.Links[host.Devices[0].Id].Writes[^1].Data;
        Assert.Equal((byte)0x50, last[0]);
        Assert.Equal((byte)0, last[2]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
