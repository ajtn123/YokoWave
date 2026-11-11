using System.Diagnostics;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>Owns event leases and declarations. Event peers never acquire or operate hardware.</summary>
public sealed class RemoteEventSession(Guid id, RemoteServerSettings settings, RemoteEventHub hub, TimeProvider? clock = null) : RemoteServerSession
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, EventLease> _leases = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = new();
    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private Task _loop = Task.CompletedTask;
    private bool _running;
    private bool _paused;
    private int _disposed;
    public override Guid Id { get; } = id;
    public override RemoteServerSettings Settings { get; } = settings;
    public override bool IsRunning { get { lock (_gate) return _running; } }
    public override Task Completion => _loop;
    public event Action<Exception>? Faulted;

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Settings.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_clock.IsRunning || Id == Guid.Empty)
                throw new LocalizedOperationException("Error.RemoteSettings");
            hub.Reset(disconnect: true);
            _clock.Start();
            _running = true;
            _loop = ExpireAsync();
        }
        return Task.CompletedTask;
    }

    internal override Task<object?> DispatchAsync(RemotePeerSession peer, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_running)
                throw new RemoteRpcException(-32000, "Error.RemoteStopped");
            if (method == "system.hello")
                return Reply(new RemoteHello(RemoteProtocol.Version, Id, Settings.LeaseMilliseconds, RemoteProtocol.MaximumMessageBytes, _clock.ElapsedMilliseconds));
            if (method is not ("lease.acquire" or "lease.release" or "lease.renew" or "event.register" or "event.emit"))
                throw new RemoteRpcException(-32601, "Error.RemoteMethod");
            if (method == "lease.acquire")
            {
                if (_paused || RemoteProtocol.Parameter<RemoteLeaseType>(parameters, "type") != RemoteLeaseType.Event)
                    throw new RemoteRpcException(-32003, "Error.RemoteLeaseType");
                if (RemoteProtocol.Parameter<string[]>(parameters, "deviceIds").Length != 0 || _leases.Values.Any(lease => lease.Peer == peer))
                    throw new RemoteRpcException(-32602, "Error.RemoteEvents");
                var acquired = new EventLease(peer);
                _leases.Add(acquired.Id, acquired);
                return Reply(new RemoteLease(acquired.Id, RemoteLeaseType.Event, [], Settings.LeaseMilliseconds));
            }
            var leaseId = RemoteProtocol.Parameter<Guid>(parameters, "lease");
            if (!_leases.TryGetValue(leaseId, out var lease) || lease.Peer != peer || lease.Age.ElapsedMilliseconds > Settings.LeaseMilliseconds)
                throw new RemoteRpcException(-32003, "Error.RemoteLease");
            if (method == "lease.release")
            {
                Remove(lease);
                return Reply(true);
            }
            if (method == "lease.renew")
            {
                lease.Age.Restart();
                return Reply(new RemoteHeartbeat(_clock.ElapsedMilliseconds));
            }
            long sequence = RemoteProtocol.Parameter<long>(parameters, "sequence");
            long deadline = RemoteProtocol.Parameter<long>(parameters, "deadline");
            if (sequence <= lease.Sequence)
                throw new RemoteRpcException(-32003, "Error.RemoteSequence");
            if (deadline < _clock.ElapsedMilliseconds || deadline > _clock.ElapsedMilliseconds + 1000)
                throw new RemoteRpcException(-32003, "Error.RemoteExpired");
            lease.Sequence = sequence;
            lease.Age.Restart();
            if (method == "event.register")
            {
                var registration = RemoteProtocol.Parameter<RemoteEventRegistration>(parameters, "registration");
                RemoteEventProtocol.Validate(registration);
                if (lease.Registration is not null)
                    throw new RemoteRpcException(-32602, "Error.RemoteEvents");
                hub.Register(lease.Id, registration);
                lease.Registration = registration;
                return Reply(true);
            }
            if (method != "event.emit")
                throw new RemoteRpcException(-32601, "Error.RemoteMethod");
            if (_paused)
                throw new RemoteRpcException(-32000, "Error.RemoteStopped");
            string eventId = RemoteProtocol.Parameter<string>(parameters, "eventId");
            var declaration = lease.Registration?.Events.FirstOrDefault(item => item.Id == eventId)
                ?? throw new RemoteRpcException(-32602, "Error.RemoteEvents");
            double value = 1;
            if (declaration.Kind == RemoteEventKind.Number)
            {
                value = RemoteProtocol.Parameter<double>(parameters, "value");
                if (!double.IsFinite(value) || value < declaration.Minimum || value > declaration.Maximum)
                    throw new RemoteRpcException(-32602, "Error.RemoteEvents");
            }
            else if (parameters.TryGetProperty("value", out _))
            {
                throw new RemoteRpcException(-32602, "Error.RemoteEvents");
            }
            return Reply(hub.Emit(lease.Id, lease.Registration!, declaration, value, _time.GetUtcNow()));
        }
    }

    internal override Task DisconnectAsync(RemotePeerSession peer)
    {
        lock (_gate)
        {
            foreach (var lease in _leases.Values.Where(lease => lease.Peer == peer).ToArray())
                Remove(lease);
        }
        return Task.CompletedTask;
    }

    public override Task SetPausedAsync(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            hub.Reset(disconnect: false);
        }
        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        lock (_gate)
        {
            if (!_running && _lifetime.IsCancellationRequested)
                return Task.CompletedTask;
            _running = false;
            _lifetime.Cancel();
            foreach (var lease in _leases.Values.ToArray())
                Remove(lease);
            hub.Reset(disconnect: true);
        }
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await StopAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    internal override void ReportFault(Exception error) => Faulted?.Invoke(error);
    private static Task<object?> Reply(object value) => Task.FromResult<object?>(value);
    private void Remove(EventLease lease)
    {
        _leases.Remove(lease.Id);
        hub.Remove(lease.Id);
        lease.Peer.Peer.Notify("lease.revoked", new { lease = lease.Id });
    }

    private async Task ExpireAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    foreach (var lease in _leases.Values.Where(lease => lease.Age.ElapsedMilliseconds > Settings.LeaseMilliseconds).ToArray())
                        Remove(lease);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private sealed class EventLease(RemotePeerSession peer)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public RemotePeerSession Peer { get; } = peer;
        public Stopwatch Age { get; } = Stopwatch.StartNew();
        public long Sequence { get; set; }
        public RemoteEventRegistration? Registration { get; set; }
    }
}
