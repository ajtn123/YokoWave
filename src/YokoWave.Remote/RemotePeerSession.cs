using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YokoWave.Remote;

internal sealed class RemotePeerSession : IAsyncDisposable
{
    private readonly RemoteExportSession _exports;
    private readonly byte[] _tokenHash;
    private volatile bool _authenticated;
    public WebSocketRpcPeer Peer { get; }
    public RemotePeerSession(WebSocket socket, RemoteExportSession exports, string token)
    {
        _exports = exports;
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        Peer = new(socket, Handle);
    }
    private Task<object?> Handle(string method, JsonElement parameters, CancellationToken ct)
    {
        if (method == "system.authenticate")
        {
            var token = RemoteProtocol.Parameter<string>(parameters, "token");
            if (token.Length > 256 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), _tokenHash))
                throw new RemoteRpcException(-32001, "Error.RemoteAuthentication");
            _authenticated = true;
            return Task.FromResult<object?>(true);
        }
        if (!_authenticated) throw new RemoteRpcException(-32001, "Error.RemoteAuthentication");
        return _exports.Dispatch(this, method, parameters, ct);
    }
    public async Task Run()
    {
        Peer.Start();
        await Task.WhenAny(Peer.Completion, Task.Delay(TimeSpan.FromSeconds(10)));
        if (!_authenticated) Peer.Abort();
        await Peer.Completion;
    }
    public async ValueTask DisposeAsync()
    {
        try { await _exports.Disconnect(this); }
        finally { await Peer.DisposeAsync(); }
    }
}
