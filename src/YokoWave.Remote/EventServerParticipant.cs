using System.Security.Cryptography;
using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.Remote;

internal sealed class EventServerParticipant(NodeRunContext context, RemoteServerSettings settings) : IRunParticipant, IOutputResource
{
    private RemoteServer? _server;
    private RemoteEventSubscription? _subscription;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var credential = context.Services.Credentials.GetOrAdd(context.NodeId, _ => new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
        var hub = RemoteEventRuntime.For(context.Services).Source(context.NodeId);
        _subscription = hub.Subscribe();
        _server = new(new RemoteEventSession(context.NodeId, settings, hub, context.Devices.Clock), credential.Token);
        await _server.StartAsync(credential.CertificatePassword, cancellationToken).ConfigureAwait(false);
        context.Services.Endpoints[context.NodeId] = new(_server.Url!.ToString(), "event");
    }
    public ValueTask CaptureAsync(PipelineInputs inputs, CancellationToken cancellationToken)
    {
        if (_server?.Session.IsRunning != true)
            throw new Core.Localization.LocalizedOperationException("Error.RemoteStopped");
        inputs.Events[context.NodeId] = _subscription?.Capture() ?? EventSignal.Empty;
        return ValueTask.CompletedTask;
    }
    public Task SetPausedAsync(bool paused) => _server?.Session.SetPausedAsync(paused) ?? Task.CompletedTask;
    public async Task StopOutputsAsync()
    {
        context.Services.Endpoints.TryRemove(context.NodeId, out _);
        try
        {
            var server = _server;
            _server = null;
            if (server is not null)
                await server.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
    public async ValueTask DisposeAsync() => await StopOutputsAsync().ConfigureAwait(false);
}
