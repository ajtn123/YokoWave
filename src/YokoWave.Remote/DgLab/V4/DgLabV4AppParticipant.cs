using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>All matching nodes in a run share an app connection and expose independent device slots.</summary>
internal sealed class DgLabV4AppParticipant(NodeRunContext context, DgLabV4AppNode settings) : IRunParticipant, IOutputResource
{
    private static readonly ConditionalWeakTable<PipelineServices, ConcurrentDictionary<string, DgLabV4AppSession>> _sessions = new();
    private DgLabV4AppSession? _session;
    private PipelineInputs? _inputs;
    private string Key => $"{context.OwnerId:N}/{DgLabV4Protocol.AppUri(settings.Url, settings.ControllerId)}";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var pool = _sessions.GetOrCreateValue(context.Services);
        _session = pool.GetOrAdd(Key, _ => new(context.Devices, settings.Url, settings.ControllerId));
        var device = context.Devices.Connections[settings.DeviceId].Device;
        if (device.IsInput && device.Supports(Core.Bluetooth.DeviceCapability.SensorStream))
            await context.Devices.ExecuteCommandAsync(device.Id, new Core.Bluetooth.ConfigureSensorStream(true), context.OwnerId, cancellationToken).ConfigureAwait(false);
        await _session.RegisterAsync(context.NodeId, device, cancellationToken).ConfigureAwait(false);
        context.Services.Endpoints[context.NodeId] = new(DgLabV4Protocol.AppUri(settings.Url, settings.ControllerId).ToString(), "dglab.v4.app");
    }

    public ValueTask CaptureAsync(PipelineInputs inputs, CancellationToken cancellationToken)
    {
        _inputs = inputs;
        _session?.Capture(context.NodeId, inputs);
        return ValueTask.CompletedTask;
    }

    public Task SetPausedAsync(bool paused)
    {
        _session?.Pause(paused);
        return Task.CompletedTask;
    }

    public async Task StopOutputsAsync()
    {
        if (_session is not { } session)
            return;
        _session = null;
        session.Unregister(context.NodeId, _inputs);
        context.Services.Endpoints.TryRemove(context.NodeId, out _);
        _inputs?.Controls.TryRemove(context.NodeId, out _);
        _inputs?.Results.TryRemove(context.NodeId, out _);
        if (session.SlotCount == 0)
        {
            _sessions.GetOrCreateValue(context.Services).TryRemove(Key, out _);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopOutputsAsync().ConfigureAwait(false);
}
