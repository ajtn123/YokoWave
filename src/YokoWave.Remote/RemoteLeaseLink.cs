using System.Net.WebSockets;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>Serializes lease operations and maintains the server clock and heartbeat.</summary>
internal sealed class RemoteLeaseLink(WebSocketRpcPeer peer, RemoteLease lease, long serverClock, long clockAt, string? deviceId = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RemoteClock _clock = new(serverClock, clockAt);
    private Task _renew = Task.CompletedTask;
    private long _sequence;
    private int _lost;
    private int _disposed;
    public Guid Id => lease.Id;
    public bool IsConnected => Volatile.Read(ref _lost) == 0 && Volatile.Read(ref _disposed) == 0 && !peer.Completion.IsCompleted;
    public string? DeviceId => deviceId;
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
            if (deviceId is not null)
                parameters["deviceId"] = deviceId;
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
        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _lost, 1) != 0)
                return;
            _lifetime.Cancel();
        }
        Disconnected?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_stateGate)
            _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
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
            _gate.Release();
            await _renew.ConfigureAwait(false);
            lock (_stateGate)
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
