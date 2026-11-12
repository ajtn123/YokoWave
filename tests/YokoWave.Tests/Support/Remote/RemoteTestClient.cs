using System.Diagnostics;
using System.Net.WebSockets;

namespace YokoWave.Tests.Support.Remote;

internal sealed class RemoteTestClient : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Dictionary<Guid, long> _sequences = [];
    private RemoteHello? _hello;
    private long _helloAt;
    public required WebSocketRpcPeer Peer { get; init; }

    public static async Task<RemoteTestClient> CreateAsync(Uri uri, bool authenticate = true)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RemoteProtocol.SubProtocol);
        RemoteTestClient? client = null;
        try
        {
            await socket.ConnectAsync(uri, Ct);
            client = new() { Peer = new(socket) };
            client.Peer.Start();
            if (authenticate)
                await client.AuthenticateAsync();
            return client;
        }
        catch (Exception error)
        {
            try
            {
                if (client is not null)
                    await client.DisposeAsync();
                else
                    socket.Dispose();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(error, cleanup);
            }
            throw;
        }
    }

    public async Task AuthenticateAsync()
    {
        await Peer.CallAsync<bool>("system.authenticate", new { token = RemoteTestHost.Token }, Ct);
        _hello = await Peer.CallAsync<RemoteHello>("system.hello", cancellationToken: Ct);
        _helloAt = Stopwatch.GetTimestamp();
    }

    public Task<RemoteLease> AcquireAsync(string deviceId, RemoteLeaseType type) => Peer.CallAsync<RemoteLease>("lease.acquire", new { deviceIds = new[] { deviceId }, type }, Ct);
    public Task<bool> ReleaseAsync(RemoteLease lease) => Peer.CallAsync<bool>("lease.release", new { lease = lease.Id }, Ct);
    public Task<T> ExecuteAsync<T>(RemoteLease lease, string method, object? args = null)
    {
        var parameters = RemoteProtocol.Element(args ?? new { }).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
        parameters["lease"] = lease.Id;
        parameters["sequence"] = _sequences[lease.Id] = _sequences.GetValueOrDefault(lease.Id) + 1;
        parameters.TryAdd("deadline", _hello!.ClockMilliseconds + (long)Stopwatch.GetElapsedTime(_helloAt).TotalMilliseconds + 500);
        return Peer.CallAsync<T>(method, parameters, Ct);
    }

    public ValueTask DisposeAsync() => Peer.DisposeAsync();
}
