using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using YokoWave.Core.Localization;

namespace YokoWave.Remote.DgLab.V4;

public sealed record DgLabV4Request(string SourceId, string Id, string Method, JsonElement Data);

/// <summary>V4 envelopes and asynchronous task responses. Waiting for a task never blocks reception or stop requests.</summary>
public sealed class DgLabV4Peer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string Client, string Id), TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _requestsGate = new();
    private readonly TaskCompletionSource<string> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JsonWebSocket? _wire;
    private Task _observe = Task.CompletedTask;
    private long _nextId;
    private int _disposed;
    private bool _controlled;
    public string ClientId { get; private set; } = "";
    public string? ControllerId { get; private set; }
    public bool IsConnected => _wire is { Completion.IsCompleted: false } && ClientId.Length > 0;
    public event Action<string, JsonElement>? FrameReceived;
    public event Action<DgLabV4Request>? Requested;
    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_wire is not null)
            throw new LocalizedOperationException("Error.AlreadyRunning");
        var socket = new ClientWebSocket();
        _controlled = uri.Query.Length > 0;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            _wire = new(socket);
            _wire.Received += Receive;
            _wire.Start();
            _observe = ObserveAsync();
            ClientId = await _hello.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (_controlled)
                await _attached.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            if (_wire is not null)
                await _wire.DisposeAsync().ConfigureAwait(false);
            else
                socket.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(string clientId, string method, object? data = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var responseTimeout = timeout ?? TimeSpan.FromSeconds(8);
        if (responseTimeout != Timeout.InfiniteTimeSpan && (responseTimeout <= TimeSpan.Zero || responseTimeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new LocalizedArgumentException("Error.RemoteSettings");
        if (!IsConnected)
            throw new LocalizedIOException("Error.RemoteDisconnected");
        string id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = (clientId, id);
        lock (_requestsGate)
        {
            int capacity = method == "device.op.clear" ? 261 : IsReplacement(method, data) ? 257 : 256;
            if (_pending.Count >= capacity)
                throw new DgLabV4Exception("queue_full");
            _pending[key] = completion;
        }
        try
        {
            SendData(clientId, data is null ? new { t = "req", reqId = id, m = method } : (object)new { t = "req", reqId = id, m = method, data });
            return await completion.Task.WaitAsync(responseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            throw new LocalizedIOException("Error.RemoteTimeout", error);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public void SendData(string? clientId, object data)
    {
        if (_wire is null)
            throw new LocalizedIOException("Error.RemoteDisconnected");
        if (ControllerId is null)
            _wire.Send(new { type = "message", clientId, data });
        else
            _wire.Send(new { type = "message", data });
    }

    public void Respond(DgLabV4Request request, object? result = null, string? error = null) =>
        SendData(request.SourceId, error is null ? new { t = "resp", reqId = request.Id, result } : (object)new { t = "resp", reqId = request.Id, error });

    public void Ping() => _wire?.Send(new { type = "ping" });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_wire is not null)
            await _wire.DisposeAsync().ConfigureAwait(false);
        await _observe.ConfigureAwait(false);
    }

    private void Receive(JsonElement frame)
    {
        string type = DgLabV4Protocol.String(frame, "type");
        if (type == "hello")
        {
            ClientId = DgLabV4Protocol.String(frame, "clientId");
            _hello.TrySetResult(ClientId);
        }
        else if (type == "controller_attached")
        {
            ControllerId = DgLabV4Protocol.String(frame, "clientId");
            _attached.TrySetResult();
        }
        else if (type == "client_disconnected")
        {
            Reject(DgLabV4Protocol.String(frame, "clientId"));
        }
        else if (type == "message")
        {
            ReceiveData(frame);
        }
        else if (type == "error")
        {
            var error = new DgLabV4Exception(DgLabV4Protocol.String(frame, "code"));
            if (frame.TryGetProperty("clientId", out var target) && target.ValueKind == JsonValueKind.String)
                Reject(target.GetString(), error);
            else
                _wire?.Abort(error);
        }
        else if (type == "ping")
        {
            _wire?.Send(new { type = "pong" });
        }
        FrameReceived?.Invoke(type, frame.Clone());
    }

    private void ReceiveData(JsonElement frame)
    {
        string source = frame.TryGetProperty("clientId", out var client) ? client.GetString() ?? "" : ControllerId ?? "";
        var data = DgLabV4Protocol.Data(frame);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("t", out var kind))
            return;
        string? type = kind.GetString();
        if (type == "resp")
        {
            string id = DgLabV4Protocol.String(data, "reqId");
            if (_pending.TryRemove((source, id), out var completion))
            {
                if (data.TryGetProperty("error", out var error))
                    completion.TrySetException(new DgLabV4Exception(error.GetString() ?? "internal_error"));
                else
                    completion.TrySetResult(data.TryGetProperty("result", out var result) ? result.Clone() : RemoteProtocol.Element(new { }));
            }
        }
        else if (type == "req")
        {
            string id = DgLabV4Protocol.String(data, "reqId");
            string method = DgLabV4Protocol.String(data, "m");
            if (id.Length is < 1 or > 256)
                throw new DgLabV4Exception("invalid_params");
            var request = new DgLabV4Request(source, id, method, DgLabV4Protocol.Data(data).Clone());
            if (Requested is { } handler)
                handler(request);
            else
                Respond(request, error: "method_not_found");
        }
    }

    private async Task ObserveAsync()
    {
        await _wire!.Completion.ConfigureAwait(false);
        var error = _wire.Error ?? new LocalizedIOException("Error.RemoteDisconnected");
        _hello.TrySetException(error);
        if (_controlled)
            _attached.TrySetException(error);
        Reject(null);
        Disconnected?.Invoke(_wire.Error);
    }

    private void Reject(string? clientId, Exception? error = null)
    {
        foreach (var entry in _pending.Where(pair => clientId is null || pair.Key.Client == clientId))
        {
            if (_pending.TryRemove(entry.Key, out var completion))
                completion.TrySetException(error ?? new LocalizedIOException("Error.RemoteDisconnected"));
        }
    }

    private static bool IsReplacement(string method, object? data)
    {
        if (method != "device.op" || data is null)
            return false;
        var operation = data is JsonElement element ? element : RemoteProtocol.Element(data);
        return operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("im", out var immediate) && immediate.ValueKind == JsonValueKind.True;
    }
}
