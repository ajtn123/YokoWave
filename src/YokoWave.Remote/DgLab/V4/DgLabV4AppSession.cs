using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>Owns one App connection; its router owns synchronized slots, requests and publication.</summary>
internal sealed class DgLabV4AppSession : IAsyncDisposable
{
    private readonly string _url;
    private readonly string _controllerId;
    private readonly SemaphoreSlim _start = new(1, 1);
    private readonly DgLabV4Peer _peer = new();
    private readonly DgLabV4AppRouter _router;
    private bool _started;
    private volatile bool _closing;
    private Exception? _error;
    public Exception? Error => Volatile.Read(ref _error);

    public DgLabV4AppSession(DeviceController devices, string url, string controllerId)
    {
        _url = url;
        _controllerId = controllerId;
        _router = new(devices, Respond, Publish);
        _peer.Requested += _router.Handle;
        _peer.FrameReceived += (type, _) =>
        {
            if (type == "controller_attached")
                _router.PublishSnapshot();
        };
        _peer.Disconnected += error =>
        {
            if (!_closing)
                Interlocked.CompareExchange(ref _error, error ?? new LocalizedIOException("Error.RemoteDisconnected"), null);
            _router.Close(send: false);
        };
    }

    public async Task RegisterAsync(Guid nodeId, DeviceDescriptor device, CancellationToken cancellationToken)
    {
        _ = DgLabV4Models.Type(device);
        _router.Add(nodeId, device);
        await _start.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                _started = true;
                await _peer.ConnectAsync(DgLabV4Protocol.AppUri(_url, _controllerId), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _router.PublishAdded(nodeId);
            }
        }
        finally
        {
            _start.Release();
        }
    }

    public void Capture(Guid nodeId, PipelineInputs inputs)
    {
        if (Error is { } error)
            throw error;
        _router.Capture(nodeId, inputs);
    }

    public void Pause(bool paused) => _router.Pause(paused);
    public void Unregister(Guid nodeId) => _router.Remove(nodeId);

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        _router.Close(send: true);
        await _peer.DisposeAsync().ConfigureAwait(false);
        _start.Dispose();
    }

    private void Publish(object value)
    {
        if (_peer.IsConnected && _peer.ControllerId is not null)
            Send(() => _peer.SendData(null, value));
    }

    private void Respond(DgLabV4Request request, object? result, string? error) =>
        Send(() => _peer.Respond(request, result, error));

    private void Send(Action send)
    {
        try
        {
            send();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // Capture propagates the failure; cleanup must still release every slot and socket.
            Interlocked.CompareExchange(ref _error, error, null);
        }
    }
}
