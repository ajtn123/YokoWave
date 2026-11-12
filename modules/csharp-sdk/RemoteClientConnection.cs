using System.Net.WebSockets;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Sdk;

/// <summary>Authenticated connection shared by remote device and event clients.</summary>
internal sealed class RemoteClientConnection(RemoteEndpointSettings settings, string token) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private volatile bool _authenticated;
    public WebSocketRpcPeer Peer { get; private set; } = null!;
    public RemoteHello Hello { get; private set; } = null!;
    public long HelloAt { get; private set; }
    public bool IsConnected => _authenticated && Peer is { Completion.IsCompleted: false };
    public event Action<string, JsonElement>? Notification;
    public event Action? Disconnected;
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        settings.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Peer is { Completion.IsCompleted: false })
                return;
            _authenticated = false;
            if (Peer is not null)
                await Peer.DisposeAsync().ConfigureAwait(false);
            var socket = new ClientWebSocket();
            WebSocketRpcPeer? connecting = null;
            socket.Options.AddSubProtocol(RemoteProtocol.SubProtocol);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await socket.ConnectAsync(new Uri(settings.Url), timeout.Token).ConfigureAwait(false);
                connecting = new(socket);
                Peer = connecting;
                Peer.Notification += (method, value) => Notification?.Invoke(method, value);
                Peer.Start();
                await Peer.CallAsync<bool>("system.authenticate", new
                {
                    token
                }, timeout.Token).ConfigureAwait(false);
                Hello = await Peer.CallAsync<RemoteHello>("system.hello", cancellationToken: timeout.Token).ConfigureAwait(false);
                HelloAt = System.Diagnostics.Stopwatch.GetTimestamp();
                if (Hello.Version != RemoteProtocol.Version)
                    throw new LocalizedOperationException("Error.RemoteVersion");
                _authenticated = true;
                _ = ObserveAsync(Peer);
            }
            catch (Exception error)
            {
                try
                {
                    if (connecting is not null)
                        await connecting.DisposeAsync().ConfigureAwait(false);
                    else
                        socket.Dispose();
                }
                catch (Exception cleanup)
                {
                    throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]);
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
    public string Key(string id) => $"ws:{settings.Id:N}:{Hello.ServerId:N}:{id}";
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _authenticated = false;
            if (Peer is not null)
                await Peer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
    private async Task ObserveAsync(WebSocketRpcPeer peer)
    {
        await peer.Completion.ConfigureAwait(false);
        if (ReferenceEquals(Peer, peer))
        {
            _authenticated = false;
            Disconnected?.Invoke();
        }
    }
}
