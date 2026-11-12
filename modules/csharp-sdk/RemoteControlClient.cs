using System.Runtime.ExceptionServices;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Sdk;

/// <summary>Direct high-level control over one renewing lease, without a local device controller or pipeline.</summary>
public sealed class RemoteControlClient : IAsyncDisposable
{
    private readonly RemoteClientConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile RemoteLeaseLink? _link;
    private volatile bool _disposed;
    public bool IsConnected => !_disposed && _connection.IsConnected;
    public bool HasLease => _link?.IsConnected == true;
    public event Action? Disconnected;
    public event Action<RemoteStatus>? StatusChanged;

    public RemoteControlClient(string url, string token)
    {
        _connection = new(new(Guid.NewGuid(), "control", url, RemoteConnectionKind.Control), token);
        _connection.Disconnected += () =>
        {
            if (_disposed)
                return;
            if (_link is { } link)
                link.Lost();
            else
                Disconnected?.Invoke();
        };
        _connection.Notification += OnNotification;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RemoteDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireConnection();
            return await _connection.Peer.CallAsync<RemoteDevice[]>("devices.list", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcquireAsync(IEnumerable<string> deviceIds, CancellationToken cancellationToken = default)
    {
        string[] targets = deviceIds.Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length is < 1 or > 32 || targets.Any(string.IsNullOrWhiteSpace))
            throw new LocalizedArgumentException("Error.RemoteLease");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireConnection();
            if (_link is not null)
                throw new LocalizedOperationException("Error.AlreadyRunning");
            var lease = await _connection.Peer.CallAsync<RemoteLease>("lease.acquire", new { deviceIds = targets, type = RemoteLeaseType.Control }, cancellationToken).ConfigureAwait(false);
            if (lease.Id == Guid.Empty || lease.Type != RemoteLeaseType.Control || lease.LeaseMilliseconds is < 1000 or > 10000 ||
                !targets.ToHashSet(StringComparer.Ordinal).SetEquals(lease.DeviceIds))
            {
                _connection.Peer.Abort();
                throw new LocalizedIOException("Error.RemoteLease");
            }
            var link = new RemoteLeaseLink(_connection.Peer, lease, _connection.Hello.ClockMilliseconds, _connection.HelloAt);
            link.Disconnected += () => Disconnected?.Invoke();
            _link = link;
            link.Start();
            if (_connection.Peer.Completion.IsCompleted)
                link.Lost();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task OutputAsync(OutputFrame frame, CancellationToken cancellationToken = default) => CallAsync<bool>("control.output", new { frame }, cancellationToken);
    public Task PlayAsync(RemoteControlProgram playback, CancellationToken cancellationToken = default) => CallAsync<bool>("control.play", new { playback }, cancellationToken);
    public Task PlayPlaylistAsync(RemoteControlProgram playback, CancellationToken cancellationToken = default) => CallAsync<bool>("control.playlist", new { playback }, cancellationToken);
    public Task PlayNativeAsync(RemoteControlProgram playback, CancellationToken cancellationToken = default) => CallAsync<bool>("control.native", new { playback }, cancellationToken);
    public Task PlayPumpAsync(RemoteControlProgram playback, CancellationToken cancellationToken = default) => CallAsync<bool>("control.pump", new { playback }, cancellationToken);
    public Task SetStrengthAsync(double channelA, double channelB, double channelC = 0, CancellationToken cancellationToken = default) => CallAsync<bool>("control.strength", new { channelA, channelB, channelC }, cancellationToken);
    public Task SetWaveformAsync(WaveformAsset waveform, CancellationToken cancellationToken = default) => CallAsync<bool>("control.waveform", new { waveform }, cancellationToken);
    public Task PauseAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("control.pause", cancellationToken: cancellationToken);
    public Task ResumeAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("control.resume", cancellationToken: cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => CallAsync<bool>("control.stop", cancellationToken: cancellationToken);
    public Task ExecuteCommandAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken = default) => CallAsync<bool>("control.command", new { deviceId, command }, cancellationToken);
    public Task<RemoteStatus> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default) => CallAsync<RemoteStatus>("control.status", new { deviceId }, cancellationToken);
    public Task SubscribeAsync(string deviceId, CancellationToken cancellationToken = default) => CallAsync<bool>("control.subscribe", new { deviceId }, cancellationToken);
    public Task UnsubscribeAsync(string deviceId, CancellationToken cancellationToken = default) => CallAsync<bool>("control.unsubscribe", new { deviceId }, cancellationToken);

    /// <summary>Calls a control method using the same serialized lease, sequence and deadline rules.</summary>
    public async Task<T> CallAsync<T>(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!method.StartsWith("control.", StringComparison.Ordinal))
            throw new LocalizedArgumentException("Error.RemoteLease");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireConnection();
            var link = _link ?? throw new LocalizedOperationException("Error.RemoteLease");
            return await link.CallAsync<T>(method, parameters ?? new { }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ReleaseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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
            try
            {
                await ReleaseCoreAsync().ConfigureAwait(false);
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
                ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReleaseCoreAsync()
    {
        var link = _link;
        if (link is null)
            return;
        _link = null;
        Exception? failure = null;
        try
        {
            await link.CallAsync<bool>("control.stop", new { }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        try
        {
            await link.ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = failure is null ? error : new LocalizedAggregateException("Error.Cleanup", [failure, error]);
        }
        if (failure is not null)
        {
            _connection.Peer.Abort();
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void RequireConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connection.IsConnected)
            throw new LocalizedOperationException("Error.RemoteDisconnected");
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method is not ("lease.revoked" or "control.status"))
            return;
        var link = _link;
        if (link is null)
            return;
        if (RemoteProtocol.Parameter<Guid>(parameters, "lease") != link.Id)
            return;
        if (method == "lease.revoked")
            link.Lost();
        else if (method == "control.status")
            StatusChanged?.Invoke(RemoteProtocol.Parameter<RemoteStatus>(parameters, "status"));
    }
}
