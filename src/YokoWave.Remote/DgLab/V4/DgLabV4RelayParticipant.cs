using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

internal sealed class DgLabV4RelayParticipant(NodeRunContext context, DgLabV4RelaySettings settings) : IRunParticipant, IOutputResource
{
    private DgLabV4Relay? _relay;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _relay = new(settings);
        string? password = context.Services.Credentials.GetValueOrDefault(context.NodeId)?.CertificatePassword;
        await _relay.StartAsync(password, cancellationToken).ConfigureAwait(false);
        context.Services.Endpoints[context.NodeId] = new(_relay.Url!.ToString(), "dglab.v4.relay");
    }
    public ValueTask CaptureAsync(PipelineInputs inputs, CancellationToken cancellationToken)
    {
        if (_relay?.Error is { } error)
            throw error;
        return ValueTask.CompletedTask;
    }
    public Task SetPausedAsync(bool paused) => paused ? StopOutputsAsync() : StartAsync(CancellationToken.None);
    public async Task StopOutputsAsync()
    {
        if (_relay is not { } relay)
            return;
        _relay = null;
        context.Services.Endpoints.TryRemove(context.NodeId, out _);
        await relay.DisposeAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync() => await StopOutputsAsync().ConfigureAwait(false);
}
