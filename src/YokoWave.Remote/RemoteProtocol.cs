using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

public enum RemoteLeaseType { Gatt, Control }
public sealed record RemoteDevice(string Id, string Name, DeviceKind Kind, string ProtocolId, int ChannelMask,
    DeviceCapability Capabilities, int? Battery, int? Signal, bool Simulated);
public sealed record RemoteHello(int Version, Guid ServerId, int LeaseMilliseconds, int MaximumMessageBytes, long ClockMilliseconds);
public sealed record RemoteHeartbeat(long ClockMilliseconds);
public sealed record RemoteLease(Guid Id, RemoteLeaseType Type, string[] DeviceIds, int LeaseMilliseconds);
public sealed record RemoteGattDescription(RemoteDevice Device, GattAttribute[] Attributes, int MaximumWriteLength);
public sealed record RemoteGattNotification(Guid Lease, string DeviceId, GattNotification Notification, long ClockMilliseconds);
public sealed record RemoteStatus(string DeviceId, DeviceTelemetry? Telemetry, int? Battery, MotionReading? Motion,
    ElectrodeContact ContactA, ElectrodeContact ContactB, bool? LightEnabled);
public sealed record RemoteControlProgram(SessionProgram Program, WaveformPreset? Waveform = null,
    double ChannelA = 0, double ChannelB = 0, double ChannelC = 0, double? DurationSeconds = null);

public sealed class RemoteRpcException(int code, string key) : Exception(L10n.Get(key)), ILocalizedError
{
    public int Code { get; } = code;
    public string Key { get; } = key;
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}

public static class RemoteProtocol
{
    public const string SubProtocol = "yokowave.v1";
    public const int MaximumMessageBytes = 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static T Read<T>(JsonElement element) => element.Deserialize<T>(Json) ?? throw new RemoteRpcException(-32602, "Error.RemoteRequest");
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    public static T Parameter<T>(JsonElement parameters, string name) => parameters.TryGetProperty(name, out var value)
        ? Read<T>(value) : throw new RemoteRpcException(-32602, "Error.RemoteRequest");
}
