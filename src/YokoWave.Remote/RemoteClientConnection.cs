using System.Net.WebSockets;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>Authenticated connection shared by device-oriented transports.</summary>
internal sealed class RemoteClientConnection(RemoteEndpointSettings settings, string token) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public WebSocketRpcPeer Peer { get; private set; } = null!;
    public RemoteHello Hello { get; private set; } = null!;
    public long HelloAt { get; private set; }
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
            if (Peer is not null)
                await Peer.DisposeAsync().ConfigureAwait(false);
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(RemoteProtocol.SubProtocol);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await socket.ConnectAsync(new Uri(settings.Url), timeout.Token).ConfigureAwait(false);
                Peer = new(socket);
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
                _ = ObserveAsync(Peer);
            }
            catch (Exception error)
            {
                try
                {
                    if (Peer is not null)
                        await Peer.DisposeAsync().ConfigureAwait(false);
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
            Disconnected?.Invoke();
    }
}

/// <summary>Serializes lease operations and maintains the server clock and heartbeat.</summary>
internal sealed class RemoteDeviceLink(WebSocketRpcPeer peer, RemoteDevice device, RemoteLease lease, long serverClock, long clockAt) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RemoteClock _clock = new(serverClock, clockAt);
    private Task _renew = Task.CompletedTask;
    private long _sequence;
    private int _lost;
    private int _disposed;
    public Guid Id => lease.Id;
    public string DeviceId => device.Id;
    public event Action? Disconnected;
    public void Start() => _renew = RenewAsync();
    public async Task<T> CallAsync<T>(string method, object args, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _lost) != 0 || Volatile.Read(ref _disposed) != 0)
                throw new LocalizedIOException("Error.RemoteDisconnected");
            var parameters = RemoteProtocol.Element(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            parameters["lease"] = lease.Id;
            parameters["deviceId"] = device.Id;
            parameters["sequence"] = ++_sequence;
            parameters["deadline"] = _clock.Now + 500;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            return await peer.CallAsync<T>(method, parameters, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
    public bool IsFresh(long clock) => Volatile.Read(ref _lost) == 0 && Volatile.Read(ref _disposed) == 0 && InputAge(clock) <= TimeSpan.FromMilliseconds(500);
    public TimeSpan InputAge(long? captured)
    {
        var expired = TimeSpan.FromDays(1);
        if (captured is not { } timestamp)
            return expired;
        double milliseconds = (double)_clock.Now - timestamp;
        return milliseconds < -500 ? expired : TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 0, expired.TotalMilliseconds));
    }
    public void Lost()
    {
        if (Interlocked.Exchange(ref _lost, 1) != 0)
            return;
        _lifetime.Cancel();
        Disconnected?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        try
        {
            if (Volatile.Read(ref _lost) == 0)
            {
                await peer.CallAsync<bool>("lease.release", new
                {
                    lease = lease.Id
                }).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is RemoteRpcException or IOException or OperationCanceledException or WebSocketException) { }
        finally
        {
            await _renew.ConfigureAwait(false);
            _lifetime.Dispose();
        }
    }
    private async Task RenewAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, lease.LeaseMilliseconds / 3)));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var heartbeat = await peer.CallAsync<RemoteHeartbeat>("lease.renew", new
                {
                    lease = lease.Id
                }, _lifetime.Token, abortOnCancellation: false).ConfigureAwait(false);
                _clock.Update(heartbeat.ClockMilliseconds);
            }
        }
        catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
        {
            if (!_lifetime.IsCancellationRequested)
                Lost();
        }
    }
}
