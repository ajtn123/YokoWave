using System.Collections.Concurrent;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Pipelines;

public interface IRunParticipant : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    ValueTask CaptureAsync(PipelineInputs inputs, CancellationToken cancellationToken);
    Task SetPausedAsync(bool paused);
}
public interface IOutputResource
{
    Task StopOutputsAsync();
}

public sealed record NodeControl(Guid Node, string Action, JsonElement Value)
{
    public static NodeControl Create<T>(Guid node, string action, T value) => new(node, action, JsonSerializer.SerializeToElement(value, PipelineJson.Options));
    public T Read<T>() => Value.Deserialize<T>(PipelineJson.Options)!;
}

public interface IInteractiveProcessor
{
    void Update(NodeControl control);
}

public interface ISuspendableProcessor
{
    void Suspend();
}

public sealed record TouchPoint(double Frequency, double PulseWidth, bool Held);
public sealed record SensorSnapshot(DeviceDescriptor Device, MotionReading? Motion, int? Steps, DeviceTelemetry? Telemetry,
    DateTimeOffset? TelemetryCapturedAt, bool Simulated);
public sealed record PipelineNodeStatus(string Phase, double Level = 0, int Count = 0, double RemainingSeconds = 0,
    Guid? Waveform = null, IReadOnlyList<WaveformSample>? Recording = null, string? Detail = null);
public sealed record PipelineCommandIntent(string DeviceId, DeviceCommand Command);

public sealed class PipelineInputs
{
    public Dictionary<Guid, AudioFeatureFrame?> Audio { get; } = [];
    public Dictionary<Guid, SensorSnapshot> Sensors { get; } = [];
    public ConcurrentDictionary<Guid, OutputFrame> Frames { get; } = new();
}

public sealed class PipelineServices(IAudioInputFactory? audio = null)
{
    public IAudioInputFactory Audio { get; } = audio ?? new UnavailableAudioInputFactory();
    public ConcurrentDictionary<Guid, PipelineCredentials> Credentials { get; } = new();
    public ConcurrentDictionary<Guid, PipelineEndpoint> Endpoints { get; } = new();
}
public sealed record PipelineCredentials(string Token, string CertificatePassword = "");
public sealed record PipelineEndpoint(string Url, string Kind);

public sealed record NodeRunContext(Guid NodeId, Guid OwnerId, DeviceController Devices, PipelineServices Services);

internal sealed class AudioParticipant(Guid node, AudioSettings settings, IAudioInputFactory factory) : IRunParticipant
{
    private IAudioInputSession? _session;
    private CancellationTokenSource? _lifetime;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _session = await factory.StartAsync(settings, _lifetime.Token).ConfigureAwait(false);
    }
    public ValueTask CaptureAsync(PipelineInputs inputs, CancellationToken cancellationToken)
    {
        if (_session is { Error: { } error })
            throw error;
        inputs.Audio[node] = _session is { Ended: false } ? _session.Latest : null;
        return ValueTask.CompletedTask;
    }
    public Task SetPausedAsync(bool paused)
    {
        _session?.SetPaused(paused);
        return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        var session = _session;
        _session = null;
        var lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Cancel();
        try
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lifetime?.Dispose();
        }
    }
}

internal sealed class SensorParticipant(NodeRunContext context, SensorNode settings) : IRunParticipant
{
    private bool _poll;
    private DateTimeOffset _nextPoll;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var device = context.Devices.Connections[settings.DeviceId].Device;
        if (device.Profile?.Motion == true)
            await context.Devices.StartMotionOwnedAsync(context.OwnerId, device.Id).ConfigureAwait(false);
        else if (device.Profile?.Supports(DeviceCapability.SensorStream) == true)
            await context.Devices.ExecuteCommandAsync(device.Id, new ConfigureSensorStream(true), context.OwnerId, cancellationToken).ConfigureAwait(false);
        else
            _poll = device.Profile?.Supports(DeviceCapability.StatusQuery) == true;
    }
    public async ValueTask CaptureAsync(PipelineInputs inputs, CancellationToken cancellationToken)
    {
        if (_poll && context.Devices.Clock.GetUtcNow() >= _nextPoll)
        {
            _nextPoll = context.Devices.Clock.GetUtcNow().AddSeconds(1);
            await context.Devices.ExecuteCommandAsync(settings.DeviceId, new QueryDevice(), context.OwnerId, cancellationToken).ConfigureAwait(false);
        }
        if (context.Devices.Connections.TryGetValue(settings.DeviceId, out var connection))
        {
            inputs.Sensors[context.NodeId] = new(connection.Device, connection.Motion, connection.Steps, connection.Telemetry,
                connection.TelemetryCapturedAt, context.Devices.AreSimulated([settings.DeviceId]));
        }
        else
        {
            inputs.Sensors.Remove(context.NodeId);
        }
    }
    public Task SetPausedAsync(bool paused) => paused ? Task.CompletedTask : StartAsync(CancellationToken.None);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // DeviceController stops streams before releasing the reservation.
}
