using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>One receive loop and one bounded send loop per connection. Mutating requests are never retried.</summary>
public sealed class WebSocketRpcPeer : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly Func<string, JsonElement, CancellationToken, Task<object?>>? _handler;
    private Task _send = Task.CompletedTask, _receive = Task.CompletedTask;
    private long _sequence;
    private long _lastRequest;
    private int _started;
    private int _disposed;
    private int _aborted;
    public Task Completion => _receive;
    public event Action<string, JsonElement>? Notification;
    public WebSocketRpcPeer(WebSocket socket, Func<string, JsonElement, CancellationToken, Task<object?>>? handler = null)
    {
        _socket = socket;
        _handler = handler;
    }
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _send = SendLoop();
        _receive = ReceiveLoop();
    }
    public async Task<T> CallAsync<T>(string method, object? parameters = null, CancellationToken ct = default, bool abortOnCancellation = true)
    {
        await _requests.WaitAsync(ct);
        var id = (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            // ID assignment and queue insertion must be atomic relative to other callers.
            await Queue(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } }, ct);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            if (abortOnCancellation) Abort();
            throw;
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally { _requests.Release(); }
        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return RemoteProtocol.Read<T>(result);
        }
        catch (TimeoutException error)
        {
            Abort();
            throw new LocalizedIOException("Error.RemoteTimeout", error);
        }
        catch (OperationCanceledException)
        {
            // Cancellation cannot retract a command already delivered to the peer.
            if (abortOnCancellation) Abort();
            throw;
        }
        finally { _pending.TryRemove(id, out _); }
    }
    public void Notify(string method, object parameters)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method, @params = parameters }, RemoteProtocol.Json);
        if (bytes.Length > RemoteProtocol.MaximumMessageBytes || !_outgoing.Writer.TryWrite(bytes)) Abort();
    }
    private async ValueTask Queue(object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, RemoteProtocol.Json);
        if (bytes.Length > RemoteProtocol.MaximumMessageBytes) throw new RemoteRpcException(-32602, "Error.RemoteMessageSize");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _outgoing.Writer.WriteAsync(bytes, linked.Token);
    }
    private async Task SendLoop()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_lifetime.Token))
                await _socket.SendAsync(message, WebSocketMessageType.Text, true, _lifetime.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { Abort(); }
    }
    private async Task ReceiveLoop()
    {
        try
        {
            var buffer = new byte[8192];
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > RemoteProtocol.MaximumMessageBytes)
                        throw new RemoteRpcException(-32600, "Error.RemoteMessageSize");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0")
                    throw new RemoteRpcException(-32600, "Error.RemoteRequest");
                if (root.TryGetProperty("method", out var method)) await HandleRequest(root, method.GetString()!);
                else HandleResponse(root);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (!_lifetime.IsCancellationRequested) System.Diagnostics.Trace.TraceWarning("WebSocket connection ended: {0}", error.Message);
        }
        finally
        {
            Abort();
            foreach (var pending in _pending.Values) pending.TrySetException(new LocalizedIOException("Error.RemoteDisconnected"));
        }
    }
    private void HandleResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !_pending.TryGetValue(id.GetString()!, out var pending)) return;
        if (root.TryGetProperty("error", out var error))
            pending.TrySetException(new RemoteRpcException(error.GetProperty("code").GetInt32(), error.GetProperty("message").GetString()!));
        else if (root.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
    }
    private async Task HandleRequest(JsonElement root, string method)
    {
        var parameters = root.TryGetProperty("params", out var value) ? value : RemoteProtocol.Element(new { });
        if (!root.TryGetProperty("id", out var id))
        {
            Notification?.Invoke(method, parameters.Clone());
            return;
        }
        if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw new RemoteRpcException(-32600, "Error.RemoteRequest");
        object? result = null;
        object? failure = null;
        try
        {
            var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
            if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var requestId) || requestId <= _lastRequest)
                throw new RemoteRpcException(-32600, "Error.RemoteSequence");
            _lastRequest = requestId;
            if (parameters.ValueKind != JsonValueKind.Object) throw new RemoteRpcException(-32602, "Error.RemoteRequest");
            if (_handler is null) throw new RemoteRpcException(-32601, "Error.RemoteMethod");
            result = await _handler(method, parameters, _lifetime.Token);
        }
        catch (Exception error)
        {
            var code = error is RemoteRpcException rpc ? rpc.Code : -32000;
            var key = error switch { RemoteRpcException rpcError => rpcError.Key, ILocalizedError localized => localized.LocalizedMessage.Key, JsonException or ArgumentException => "Error.RemoteRequest", _ => "Error.RemoteOperation" };
            failure = new { code, message = key };
            System.Diagnostics.Trace.TraceWarning("Remote operation {0} failed: {1}", method, error);
        }
        await Queue(failure is null ? new { jsonrpc = "2.0", id = id.Clone(), result } : (object)new { jsonrpc = "2.0", id = id.Clone(), error = failure }, _lifetime.Token);
    }
    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
        _lifetime.Cancel();
        _outgoing.Writer.TryComplete();
        _socket.Abort();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Abort();
        await Task.WhenAll(_send, _receive);
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
