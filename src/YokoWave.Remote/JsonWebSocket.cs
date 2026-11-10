using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>Bounded JSON text framing shared by protocol peers; one reader and one writer own the socket.</summary>
internal sealed class JsonWebSocket(WebSocket socket, bool ignoreInvalidJson = false) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true });
    private Task _writer = Task.CompletedTask;
    private Task _reader = Task.CompletedTask;
    private int _started;
    private int _disposed;
    public Task Completion => _completed.Task;
    public Exception? Error { get; private set; }
    public event Action<JsonElement>? Received;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _writer = WriteAsync();
        _reader = ReadAsync();
    }

    public void Send(object message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, RemoteProtocol.Json);
        if (bytes.Length > RemoteProtocol.MaximumMessageBytes || !_outgoing.Writer.TryWrite(bytes))
        {
            var error = new LocalizedIOException("Error.RemoteMessageSize");
            Abort(error);
            throw error;
        }
    }

    public void Abort(Exception? error = null)
    {
        Error ??= error;
        _lifetime.Cancel();
        _outgoing.Writer.TryComplete();
        socket.Abort();
    }

    public async Task CloseAsync(int code, string reason)
    {
        _outgoing.Writer.TryComplete();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _writer.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, timeout.Token).ConfigureAwait(false);
            // Let the peer receive queued errors and the close frame before disposing the underlying connection.
            await _reader.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            Abort(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Abort();
        await Task.WhenAll(_reader, _writer).ConfigureAwait(false);
        _completed.TrySetResult();
        socket.Dispose();
        _lifetime.Dispose();
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (byte[] message in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                await socket.SendAsync(message, WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Abort(_lifetime.IsCancellationRequested ? null : error);
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            byte[] buffer = new byte[8192];
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > RemoteProtocol.MaximumMessageBytes)
                        throw new LocalizedIOException("Error.RemoteMessageSize");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = Parse(message.ToArray());
                if (document is not null)
                    Received?.Invoke(document.RootElement);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Error ??= _lifetime.IsCancellationRequested ? null : error;
        }
        finally
        {
            Abort();
            _completed.TrySetResult();
        }
    }

    private JsonDocument? Parse(byte[] message)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException) when (ignoreInvalidJson)
        {
            // The V4 relay ignores invalid JSON; typed peers remain strict.
            return null;
        }
        if (document.RootElement.ValueKind == JsonValueKind.Object)
            return document;
        document.Dispose();
        if (ignoreInvalidJson)
            return null;
        throw new LocalizedIOException("Error.RemoteRequest");
    }
}
