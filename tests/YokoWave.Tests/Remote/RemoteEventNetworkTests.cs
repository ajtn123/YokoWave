using YokoWave.Core.Bluetooth;
using YokoWave.Remote;
using YokoWave.Tests.Support.Remote;

namespace YokoWave.Tests.Remote;

public sealed class RemoteEventNetworkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task TypedClientDeclaresEventsBeforeEmittingAndClientIdentitiesRemainIsolated()
    {
        await using var host = await RemoteEventTestHost.CreateAsync();
        await using var first = host.Client("dinosaur");
        await using var second = host.Client("keyboard");
        await first.ConnectAsync(Ct);
        await second.ConnectAsync(Ct);
        Assert.True(first.IsConnected);
        Assert.Equal(4, host.Hub.Snapshot().Count);
        var trigger = await first.EmitAsync("collision", cancellationToken: Ct);
        var number = await second.EmitAsync("score", 75, Ct);
        Assert.True(number.Sequence > trigger.Sequence);
        await Assert.ThrowsAsync<RemoteRpcException>(() => first.EmitAsync("unlisted", cancellationToken: Ct));
        await Assert.ThrowsAsync<RemoteRpcException>(() => first.EmitAsync("score", 101, Ct));
        await Assert.ThrowsAsync<RemoteRpcException>(() => first.EmitAsync("collision", .5, Ct));
        await using var duplicate = host.Client("dinosaur");
        Assert.Equal("Error.RemoteEventClientInUse", (await Assert.ThrowsAsync<RemoteRpcException>(() => duplicate.ConnectAsync(Ct))).Key);
        await first.EmitAsync("collision", cancellationToken: Ct);
        Assert.True(first.IsConnected);
    }

    [Fact]
    public async Task ServerRejectsUnregisteredEventsInvalidDeclarationsAndOtherLeaseTypes()
    {
        await using var host = await RemoteEventTestHost.CreateAsync();
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!, authenticate: false);
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Peer.CallAsync<RemoteHello>("system.hello", cancellationToken: Ct));
        await client.AuthenticateAsync();
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Peer.CallAsync<RemoteLease>("lease.acquire", new { deviceIds = Array.Empty<string>(), type = "control" }, Ct));
        var lease = await client.Peer.CallAsync<RemoteLease>("lease.acquire", new { deviceIds = Array.Empty<string>(), type = "event" }, Ct);
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<RemoteEventReceipt>(lease, "event.emit", new { eventId = "collision" }));
        var invalid = new RemoteEventRegistration("game", [new("same", RemoteEventKind.Trigger), new("same", RemoteEventKind.Number)]);
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "event.register", new { registration = invalid }));
        Assert.Empty(host.Hub.Snapshot());
        var valid = new RemoteEventRegistration("game", [new("collision", RemoteEventKind.Trigger), new("score", RemoteEventKind.Number, Minimum: 0, Maximum: 100)]);
        await client.ExecuteAsync<bool>(lease, "event.register", new { registration = valid });
        Assert.Equal(2, host.Hub.Snapshot().Count);
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "event.register", new { registration = valid }));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.Peer.CallAsync<RemoteEventReceipt>("event.emit", new { lease = lease.Id, sequence = 0, deadline = 0, eventId = "collision" }, Ct));
        await using var other = await RemoteTestClient.CreateAsync(host.Server.Url!);
        await Assert.ThrowsAsync<RemoteRpcException>(() => other.ExecuteAsync<RemoteEventReceipt>(lease, "event.emit", new { eventId = "collision" }));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<RemoteEventReceipt>(lease, "event.emit", new { eventId = "missing" }));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<RemoteEventReceipt>(lease, "event.emit", new { eventId = "score", value = -1 }));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<RemoteEventReceipt>(lease, "event.emit", new { eventId = "collision", value = 1 }));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "gatt.write"));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<bool>(lease, "control.output"));
        await client.ExecuteAsync<RemoteEventReceipt>(lease, "event.emit", new { eventId = "collision" });
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.ExecuteAsync<RemoteEventReceipt>(lease, "event.emit", new { eventId = "collision", deadline = -1 }));
    }

    [Fact]
    public async Task EventLeaseExpiresAndCachedDeclarationsCanBeReusedAfterDisconnect()
    {
        await using var host = await RemoteEventTestHost.CreateAsync(1000);
        await using var renewing = host.Client("renewing");
        await renewing.ConnectAsync(Ct);
        await using var raw = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var lease = await raw.Peer.CallAsync<RemoteLease>("lease.acquire", new { type = "event", deviceIds = Array.Empty<string>() }, Ct);
        await raw.ExecuteAsync<bool>(lease, "event.register", new { registration = new RemoteEventRegistration("game", [new("collision", RemoteEventKind.Trigger)]) });
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.Peer.Notification += (method, _) => { if (method == "lease.revoked") expired.TrySetResult(); };
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await Assert.ThrowsAsync<RemoteRpcException>(() => raw.ExecuteAsync<RemoteEventReceipt>(lease, "event.emit", new { eventId = "collision" }));
        Assert.Contains(host.Hub.Snapshot(), item => item.ClientId == "game");
        Assert.True(renewing.IsConnected);
        await renewing.EmitAsync("collision", cancellationToken: Ct);
        await using var replacement = host.Client("game");
        await replacement.ConnectAsync(Ct);
        await replacement.EmitAsync("collision", cancellationToken: Ct);
    }

    [Fact]
    public async Task TypedClientReportsDisconnectAndCanReconnectWithFreshDeclarations()
    {
        await using var host = await RemoteEventTestHost.CreateAsync();
        await using var client = host.Client("game");
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += () => disconnected.TrySetResult();
        await client.ConnectAsync(Ct);
        await host.Server.DisposeAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.False(client.IsConnected);
        var hub = new RemoteEventHub();
        await using var replacement = new RemoteServer(new RemoteEventSession(host.Server.Session.Id, new(Port: host.Server.Url!.Port), hub), RemoteTestHost.Token);
        await replacement.StartAsync(cancellationToken: Ct);
        await client.ConnectAsync(Ct);
        Assert.True(client.IsConnected);
        Assert.Equal(2, hub.Snapshot().Count);
        await client.EmitAsync("collision", cancellationToken: Ct);
        await client.DisposeAsync();
        await client.DisposeAsync();
        Assert.False(client.IsConnected);
        await replacement.Session.StopAsync();
        await replacement.Session.DisposeAsync();
        await replacement.Session.StopAsync();
    }

    [Fact]
    public async Task CatalogEvictsOldInactiveClientsWithoutRemovingActiveDeclarations()
    {
        await using var host = await RemoteEventTestHost.CreateAsync();
        await using var active = host.Client("active");
        await active.ConnectAsync(Ct);
        await using var changing = await RemoteTestClient.CreateAsync(host.Server.Url!);
        for (int index = 0; index < 129; index++)
        {
            var lease = await changing.Peer.CallAsync<RemoteLease>("lease.acquire", new { type = "event", deviceIds = Array.Empty<string>() }, Ct);
            await changing.ExecuteAsync<bool>(lease, "event.register", new { registration = new RemoteEventRegistration($"guest{index}", [new("event", RemoteEventKind.Trigger)]) });
            await changing.ReleaseAsync(lease);
        }
        var catalog = host.Hub.Snapshot();
        Assert.Equal(128, catalog.Select(item => item.ClientId).Distinct().Count());
        Assert.DoesNotContain(catalog, item => item.ClientId == "guest0");
        Assert.Contains(catalog, item => item.ClientId == "guest128");
        Assert.Equal(2, catalog.Count(item => item.ClientId == "active"));
        await active.EmitAsync("collision", cancellationToken: Ct);
    }

    [Fact]
    public async Task DeviceEndpointsRejectEventLeases()
    {
        await using var host = await RemoteTestHost.CreateAsync(BluetoothProtocol.CoyoteV3);
        await using var client = await RemoteTestClient.CreateAsync(host.Server.Url!);
        var device = Assert.Single(await client.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: Ct));
        await Assert.ThrowsAsync<RemoteRpcException>(() => client.AcquireAsync(device.Id, RemoteLeaseType.Event));
    }
}
