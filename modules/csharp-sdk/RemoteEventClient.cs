using YokoWave.Core.Localization;

namespace YokoWave.Sdk;

/// <summary>Authenticates, declares the event catalog, then emits declared events through a renewing event lease.</summary>
public sealed class RemoteEventClient : IAsyncDisposable
{
    private readonly RemoteClientConnection _connection;
    private readonly RemoteEventRegistration _registration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile RemoteLeaseLink? _link;
    private bool _disposed;
    public bool IsConnected => _link?.IsConnected == true;
    public event Action? Disconnected;

    public RemoteEventClient(string url, string token, RemoteEventRegistration registration)
    {
        RemoteEventProtocol.Validate(registration);
        _registration = registration with { Events = registration.Events.ToArray() };
        _connection = new(new(Guid.NewGuid(), registration.ClientId, url), token);
        _connection.Disconnected += () => _link?.Lost();
        _connection.Notification += (method, value) =>
        {
            if (method == "lease.revoked" && _link is { } link && RemoteProtocol.Parameter<Guid>(value, "lease") == link.Id)
                link.Lost();
        };
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_link?.IsConnected == true)
                throw new LocalizedOperationException("Error.AlreadyRunning");
            if (_link is { } previous)
            {
                _link = null;
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            await _connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var lease = await _connection.Peer.CallAsync<RemoteLease>("lease.acquire", new { type = RemoteLeaseType.Event, deviceIds = Array.Empty<string>() }, cancellationToken).ConfigureAwait(false);
            var link = new RemoteLeaseLink(_connection.Peer, lease, _connection.Hello.ClockMilliseconds, _connection.HelloAt);
            link.Disconnected += () => Disconnected?.Invoke();
            try
            {
                await link.CallAsync<bool>("event.register", new { registration = _registration }, cancellationToken).ConfigureAwait(false);
                _link = link;
                link.Start();
                if (_connection.Peer.Completion.IsCompleted)
                    link.Lost();
            }
            catch (Exception error)
            {
                _link = null;
                try
                {
                    await link.DisposeAsync().ConfigureAwait(false);
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

    public Task<RemoteEventReceipt> EmitAsync(string eventId, double? value = null, CancellationToken cancellationToken = default)
    {
        var declaration = _registration.Events.FirstOrDefault(item => item.Id == eventId) ?? throw new RemoteRpcException(-32602, "Error.RemoteEvents");
        if (declaration.Kind == RemoteEventKind.Number ? value is null || !double.IsFinite(value.Value) || value < declaration.Minimum || value > declaration.Maximum : value.HasValue)
            throw new RemoteRpcException(-32602, "Error.RemoteEvents");
        var link = _link ?? throw new LocalizedIOException("Error.RemoteDisconnected");
        object args = value.HasValue ? new { eventId, value = value.Value } : new { eventId };
        return link.CallAsync<RemoteEventReceipt>("event.emit", args, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            Exception? failure = null;
            var link = _link;
            _link = null;
            try
            {
                if (link is not null)
                    await link.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = error;
            }
            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failure = failure is null ? error : new LocalizedAggregateException("Error.Cleanup", [failure, error]);
            }
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            _gate.Release();
        }
    }
}
