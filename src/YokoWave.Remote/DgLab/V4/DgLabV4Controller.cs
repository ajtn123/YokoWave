using System.Text.Json;

namespace YokoWave.Remote.DgLab.V4;

public sealed record DgLabV4TaskOptions(int Priority = 1, double Duration = 0, bool Immediate = false, TimeSpan? Timeout = null);

/// <summary>Typed controller operations; connection ownership remains with the peer's owner.</summary>
public sealed class DgLabV4Controller(DgLabV4Peer peer)
{
    public Task<JsonElement> GetDevicesAsync(string appId, CancellationToken cancellationToken = default) =>
        peer.RequestAsync(appId, "devices.get", cancellationToken: cancellationToken);

    public Task<JsonElement> PingAsync(string appId, CancellationToken cancellationToken = default) =>
        peer.RequestAsync(appId, "ping", cancellationToken: cancellationToken);

    public Task<JsonElement> AddIntensityAsync(string appId, string slotId, int channel, double delta,
        DgLabV4TaskOptions? options = null, CancellationToken cancellationToken = default) =>
        OperateAsync(appId, Operation(slotId, 3, channel, RemoteProtocol.Element(delta), options), options?.Timeout, cancellationToken);

    public Task<JsonElement> SetTemporaryIntensityAsync(string appId, string slotId, int channel, double value,
        DgLabV4TaskOptions? options = null, CancellationToken cancellationToken = default) =>
        OperateAsync(appId, Operation(slotId, 4, channel, RemoteProtocol.Element(value), options), options?.Timeout, cancellationToken);

    public Task<JsonElement> ResetIntensityAsync(string appId, string slotId, int channel,
        DgLabV4TaskOptions? options = null, CancellationToken cancellationToken = default) =>
        OperateAsync(appId, Operation(slotId, 7, channel, RemoteProtocol.Element(0), options), options?.Timeout, cancellationToken);

    public Task<JsonElement> SendPulseAsync(string appId, string slotId, int channel, JsonElement frames,
        int version = 3, int? sequence = null, DgLabV4TaskOptions? options = null, CancellationToken cancellationToken = default) =>
        OperateAsync(appId, Operation(slotId, 0, channel, frames, options) with { Version = version, Sequence = sequence }, options?.Timeout, cancellationToken);

    public Task<JsonElement> OperateAsync(string appId, DgLabV4Operation operation, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        operation.Validate();
        return peer.RequestAsync(appId, "device.op", operation, timeout ?? CompletionTimeout(operation), cancellationToken);
    }

    public Task<JsonElement> ClearAsync(string appId, string? slotId = null, int? channel = null, CancellationToken cancellationToken = default)
    {
        if (channel is < 0 or > 1 || channel.HasValue && slotId is null || slotId is { Length: 0 or > 256 })
            throw new DgLabV4Exception("invalid_params");
        object? data = slotId is null ? null : channel.HasValue ? new { s = slotId, c = channel.Value } : new { s = slotId };
        return peer.RequestAsync(appId, "device.op.clear", data, cancellationToken: cancellationToken);
    }

    private static DgLabV4Operation Operation(string slotId, int type, int channel, JsonElement value, DgLabV4TaskOptions? options) =>
        new(slotId, type, channel, value, options?.Priority ?? 1, options?.Duration ?? 0, options?.Immediate ?? false);

    private static TimeSpan CompletionTimeout(DgLabV4Operation operation)
    {
        if (operation.Type == 4 && operation.Duration == 0)
            return Timeout.InfiniteTimeSpan;
        double duration = operation.Type == 0 ? operation.Value.GetArrayLength() * 100 : operation.Duration;
        if (operation.Duration > 0 && operation.Type == 0)
            duration = Math.Min(duration, operation.Duration);
        return TimeSpan.FromMilliseconds(Math.Max(8000, duration + 1000));
    }
}
