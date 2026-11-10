using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Remote.DgLab.V4;

namespace YokoWave.Tests.Remote;

public sealed class DgLabV4NetworkTests
{
    [Fact]
    public async Task RelaySupportsMultipleAppsAndLongTaskRepliesDoNotBlockStopRequests()
    {
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: Ct);
        await using var controller = new DgLabV4Peer();
        await controller.ConnectAsync(relay.Url!, Ct);
        await using var first = new DgLabV4Peer();
        await using var second = new DgLabV4Peer();
        var held = new TaskCompletionSource<DgLabV4Request>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Requested += request =>
        {
            if (request.Method == "device.op")
                held.TrySetResult(request);
            else
                first.Respond(request, new { cleared = true });
        };
        second.Requested += request => second.Respond(request, new { second = true });
        await first.ConnectAsync(DgLabV4Protocol.AppUri(relay.Url!.ToString(), controller.ClientId), Ct);
        await second.ConnectAsync(DgLabV4Protocol.AppUri(relay.Url!.ToString(), controller.ClientId), Ct);
        var pending = controller.RequestAsync(first.ClientId, "device.op", cancellationToken: Ct);
        var operation = await held.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        var stopped = await controller.RequestAsync(first.ClientId, "device.op.clear", cancellationToken: Ct);
        Assert.True(stopped.GetProperty("cleared").GetBoolean());
        Assert.False(pending.IsCompleted);
        var other = await controller.RequestAsync(second.ClientId, "ping", cancellationToken: Ct);
        Assert.True(other.GetProperty("second").GetBoolean());
        first.Respond(operation, new { reason = "cleared" });
        Assert.Equal("cleared", (await pending).GetProperty("reason").GetString());
        await controller.DisposeAsync();
        await AsyncAssert.EventuallyAsync(() => !first.IsConnected && !second.IsConnected);
    }

    [Fact]
    public async Task DeviceSourceWaitsForPairingAndReceivesSnapshotsWithoutRestartingConnection()
    {
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: Ct);
        await using var source = new DgLabV4Transport(Guid.NewGuid(), relay.Url!.ToString());
        Assert.Empty(await source.ScanAsync(Ct));
        Assert.Contains("action=socket", source.PairingUrl);
        var appUri = new Uri(Uri.UnescapeDataString(source.PairingUrl.Split("&url=")[1]));
        await using var app = new DgLabV4Peer();
        ConcurrentQueue<double> levels = new();
        app.Requested += request =>
        {
            if (request.Method == "device.op" && request.Data.GetProperty("t").GetInt32() == 4 && request.Data.GetProperty("c").GetInt32() == 0)
                levels.Enqueue(request.Data.GetProperty("v").GetDouble());
            app.Respond(request, new { devices = new[] { new DgLabV4Device("one", "Coyote", "COYOTE_030") } });
        };
        await app.ConnectAsync(appUri, Ct);
        app.SendData(null, new { t = "ev", ev = "devices.snapshot", devices = new[] { new { slotId = "one", name = "Coyote", type = "COYOTE_030", slotState = new { hasDevice = true } } } });
        await AsyncAssert.EventuallyAsync(() => source.Slots.Count == 1 && source.Slots[0].Online);
        var device = Assert.Single(await source.ScanAsync(Ct));
        Assert.Equal("Coyote", device.Name);
        await source.ConnectAsync(device, Ct);
        await source.SendAsync(device.Id, new OutputFrame(80, 0, 20, ReferenceSample: new(.25, 20, 1)), Ct);
        await AsyncAssert.EventuallyAsync(() => levels.Count == 1);
        Assert.Equal(40, Assert.Single(levels));
        app.SendData(null, new { t = "ev", ev = "devices.patch", removed = new[] { "one" } });
        await AsyncAssert.EventuallyAsync(() => source.Slots.Count == 0);
        Assert.True(source.IsConnected);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RelayRejectsUnknownControllersAndKeepsControllerRoutesIsolated()
    {
        await using var relay = new DgLabV4Relay(new(Port: 0, Path: "/custom"));
        await relay.StartAsync(cancellationToken: Ct);
        await using var invalid = new DgLabV4Peer();
        var missing = await Assert.ThrowsAsync<DgLabV4Exception>(() => invalid.ConnectAsync(DgLabV4Protocol.AppUri(relay.Url!.ToString(), "missing"), Ct));
        Assert.Equal("controller_not_found", missing.Code);
        await using var first = new DgLabV4Peer();
        await using var second = new DgLabV4Peer();
        await first.ConnectAsync(relay.Url!, Ct);
        await second.ConnectAsync(relay.Url!, Ct);
        await using var app = new DgLabV4Peer();
        app.Requested += request => app.Respond(request, new { ok = true });
        await app.ConnectAsync(DgLabV4Protocol.AppUri(relay.Url!.ToString(), first.ClientId), Ct);
        var error = await Assert.ThrowsAsync<DgLabV4Exception>(() => second.RequestAsync(app.ClientId, "ping", cancellationToken: Ct));
        Assert.Equal("client_not_found", error.Code);
        Assert.True((await first.RequestAsync(app.ClientId, "ping", cancellationToken: Ct)).GetProperty("ok").GetBoolean());
        using var http = new HttpClient();
        var root = new UriBuilder(relay.Url!) { Scheme = "http", Path = "/" }.Uri;
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync(root, Ct)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.UpgradeRequired, (await http.GetAsync(new Uri(root, "/custom"), Ct)).StatusCode);
    }

    [Fact]
    public async Task EmptyControllerReceivesHeartbeatAndIdleTimeout()
    {
        await using var relay = new DgLabV4Relay(new(Port: 0, HeartbeatMilliseconds: 100, IdleMilliseconds: 300));
        await relay.StartAsync(cancellationToken: Ct);
        await using var controller = new DgLabV4Peer();
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.FrameReceived += (type, _) =>
        {
            if (type == "heartbeat")
                heartbeat.TrySetResult();
            if (type == "idle_timeout")
                expired.TrySetResult();
        };
        await controller.ConnectAsync(relay.Url!, Ct);
        await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await expired.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        await AsyncAssert.EventuallyAsync(() => !controller.IsConnected);
        Assert.Null(relay.Error);
    }

    [Fact]
    public async Task ClearingRemainsAvailableWhenAllRegularRequestSlotsAreOccupied()
    {
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: Ct);
        await using var peer = new DgLabV4Peer();
        await peer.ConnectAsync(relay.Url!, Ct);
        await using var app = new DgLabV4Peer();
        int received = 0;
        app.Requested += request =>
        {
            if (request.Method == "device.op.clear")
                app.Respond(request, new { });
            else
                Interlocked.Increment(ref received);
        };
        await app.ConnectAsync(DgLabV4Protocol.AppUri(relay.Url!.ToString(), peer.ClientId), Ct);
        var pending = Enumerable.Range(0, 256).Select(_ => peer.RequestAsync(app.ClientId, "device.op", new { }, Timeout.InfiniteTimeSpan, Ct)).ToArray();
        await AsyncAssert.EventuallyAsync(() => Volatile.Read(ref received) == 256);
        var full = await Assert.ThrowsAsync<DgLabV4Exception>(() => peer.RequestAsync(app.ClientId, "device.op", cancellationToken: Ct));
        Assert.Equal("queue_full", full.Code);
        await new DgLabV4Controller(peer).ClearAsync(app.ClientId, cancellationToken: Ct);
        await peer.DisposeAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => Task.WhenAll(pending));
    }

    [Fact]
    public async Task RelayIgnoresMalformedFramesAndPreservesOpaquePayloads()
    {
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: Ct);
        using var controller = new ClientWebSocket();
        await controller.ConnectAsync(relay.Url!, Ct);
        string id = (await ReceiveAsync(controller)).GetProperty("clientId").GetString()!;
        foreach (string message in new[] { "not-json", "[]", "{}", "{\"type\":7}", "{\"type\":\"ping\"}" })
            await controller.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, Ct);
        Assert.Equal("pong", (await ReceiveAsync(controller)).GetProperty("type").GetString());
        await using var app = new DgLabV4Peer();
        var forwarded = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.FrameReceived += (type, frame) =>
        {
            if (type == "message")
                forwarded.TrySetResult(frame.GetProperty("data").Clone());
        };
        await app.ConnectAsync(DgLabV4Protocol.AppUri(relay.Url!.ToString(), id), Ct);
        string raw = JsonSerializer.Serialize(new { type = "message", clientId = app.ClientId, data = new[] { 1, 2, 3 } });
        await controller.SendAsync(Encoding.UTF8.GetBytes(raw), WebSocketMessageType.Text, true, Ct);
        Assert.Equal(new[] { 1, 2, 3 }, (await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct)).EnumerateArray().Select(item => item.GetInt32()));
    }

    [Fact]
    public async Task RelaySupportsPfxTlsWithoutDisablingClientTrustValidation()
    {
        using var folder = new TestFolder();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string path = Path.Combine(folder.Path, "test.pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "test-only"), Ct);
        await using var relay = new DgLabV4Relay(new(Port: 0, CertificatePath: path));
        await relay.StartAsync("test-only", Ct);
        Assert.Equal("wss", relay.Url!.Scheme);
        await using var untrusted = new DgLabV4Peer();
        await Assert.ThrowsAsync<WebSocketException>(() => untrusted.ConnectAsync(relay.Url, Ct));
        using var trusted = new ClientWebSocket();
        trusted.Options.RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString();
        await trusted.ConnectAsync(relay.Url, Ct);
        Assert.Equal("hello", (await ReceiveAsync(trusted)).GetProperty("type").GetString());
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        byte[] buffer = new byte[8192];
        using var bytes = new MemoryStream();
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
            bytes.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        using var document = JsonDocument.Parse(bytes.ToArray());
        return document.RootElement.Clone();
    }
}
