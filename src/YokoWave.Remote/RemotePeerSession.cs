using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YokoWave.Remote;

internal sealed class RemotePeerSession : IAsyncDisposable
{
    private readonly RemoteServerSession _session;
    private readonly byte[] _tokenHash;
    private volatile bool _authenticated;
    public WebSocketRpcPeer Peer { get; }
    public RemotePeerSession(WebSocket socket, RemoteServerSession session, string token)
    {
        _session = session;
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        Peer = new(socket, HandleAsync);
    }
    public async Task RunAsync()
    {
        Peer.Start();
        await Task.WhenAny(Peer.Completion, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        if (!_authenticated)
            Peer.Abort();
        await Peer.Completion.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.DisconnectAsync(this).ConfigureAwait(false);
        }
        finally
        {
            await Peer.DisposeAsync().ConfigureAwait(false);
        }
    }
    private Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method == "system.authenticate")
        {
            string token = RemoteProtocol.Parameter<string>(parameters, "token");
            if (token.Length > 256 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), _tokenHash))
                throw new RemoteRpcException(-32001, "Error.RemoteAuthentication");
            _authenticated = true;
            return Task.FromResult<object?>(true);
        }
        if (!_authenticated)
            throw new RemoteRpcException(-32001, "Error.RemoteAuthentication");
        return _session.DispatchAsync(this, method, parameters, cancellationToken);
    }
}
