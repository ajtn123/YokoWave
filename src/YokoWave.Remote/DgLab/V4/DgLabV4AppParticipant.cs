using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>All matching nodes in a run share an app connection and expose independent device slots.</summary>
internal sealed class DgLabV4AppParticipant(NodeRunContext context, DgLabV4AppNode settings) : IRunParticipant, IOutputResource
{
    private DgLabV4AppPool.Lease? _lease;
    private PipelineInputs? _inputs;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lease = DgLabV4AppPool.For(context.Services).Acquire(context, settings);
        var device = context.Devices.Connections[settings.DeviceId].Device;
        if (device.IsInput && device.Supports(Core.Bluetooth.DeviceCapability.SensorStream))
            await context.Devices.ExecuteCommandAsync(device.Id, new Core.Bluetooth.ConfigureSensorStream(true), context.OwnerId, cancellationToken).ConfigureAwait(false);
        await _lease.Session.RegisterAsync(context.NodeId, device, cancellationToken).ConfigureAwait(false);
        context.Services.Endpoints[context.NodeId] = new(DgLabV4Protocol.AppUri(settings.Url, settings.ControllerId).ToString(), "dglab.v4.app");
    }

    public ValueTask CaptureAsync(PipelineInputs inputs, CancellationToken cancellationToken)
    {
        _inputs = inputs;
        _lease?.Session.Capture(context.NodeId, inputs);
        return ValueTask.CompletedTask;
    }

    public Task SetPausedAsync(bool paused)
    {
        _lease?.Session.Pause(paused);
        return Task.CompletedTask;
    }

    public async Task StopOutputsAsync()
    {
        if (_lease is not { } lease)
            return;
        _lease = null;
        List<Exception> errors = [];
        try
        {
            lease.Session.Unregister(context.NodeId);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        context.Services.Endpoints.TryRemove(context.NodeId, out _);
        _inputs?.Controls.TryRemove(context.NodeId, out _);
        _inputs?.Results.TryRemove(context.NodeId, out _);
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }

    public async ValueTask DisposeAsync() => await StopOutputsAsync().ConfigureAwait(false);
}
