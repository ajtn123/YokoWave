using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Sdk;

public enum RemoteLeaseType
{
    Gatt, Control, Event
}
public sealed record RemoteDevice(string Id, string Name, DeviceKind Kind, string ProtocolId, int ChannelMask,
    DeviceCapability Capabilities, int? Battery, int? Signal, bool Simulated);
public sealed record RemoteHello(int Version, Guid ServerId, int LeaseMilliseconds, int MaximumMessageBytes, long ClockMilliseconds);
public sealed record RemoteHeartbeat(long ClockMilliseconds);
public sealed record RemoteLease(Guid Id, RemoteLeaseType Type, string[] DeviceIds, int LeaseMilliseconds);
public sealed record RemoteGattDescription(RemoteDevice Device, GattAttribute[] Attributes, int MaximumWriteLength);
public sealed record RemoteGattNotification(Guid Lease, string DeviceId, GattNotification Notification, long ClockMilliseconds);
public sealed record RemoteStatus(string DeviceId, DeviceTelemetry? Telemetry, int? Battery, MotionReading? Motion,
    ElectrodeContact ContactA, ElectrodeContact ContactB, bool? LightEnabled, byte? HardwareVersion = null, byte? SoftwareVersion = null,
    long? TelemetryClockMilliseconds = null, long? MotionClockMilliseconds = null,
    long? PressureClockMilliseconds = null, long? EventClockMilliseconds = null);
public enum RemotePlaybackKind
{
    Waveform, NativeProgram, Pump
}
public sealed record RemotePlaybackSettings(RemotePlaybackKind Kind = RemotePlaybackKind.Waveform,
    PlaybackOrder Order = PlaybackOrder.Repeat, IReadOnlyList<WaveformAsset>? Playlist = null, WaveformAsset? ChannelBWaveform = null,
    double OutputLimit = 60, StrengthSettings? Strength = null, bool OffsetChannelB = true, bool Gentle = false,
    PumpOutput? Pump = null, NativeProgramSettings? NativeProgram = null, HardwareOutputSettings? Hardware = null, DgBalanceSettings? Balances = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Order) || !double.IsFinite(OutputLimit) || OutputLimit is < 0 or > 100 || Playlist?.Count > 100)
            throw new LocalizedArgumentException("Error.PlaybackSettings");
        Strength?.Validate();
        Pump?.Validate();
        NativeProgram?.Validate();
        Hardware?.Validate();
        ChannelBWaveform?.Validate();
        foreach (var waveform in Playlist ?? [])
            waveform.Validate();
    }
}
public sealed record RemoteControlProgram(RemotePlaybackSettings Program, WaveformAsset? Waveform = null,
    double ChannelA = 0, double ChannelB = 0, double ChannelC = 0, double? DurationSeconds = null);

public sealed class RemoteRpcException(int code, string key, Exception? innerException = null) : Exception(L10n.Get(key), innerException), ILocalizedError
{
    public int Code { get; } = code;
    public string Key { get; } = key;
    public LocalizedMessage LocalizedMessage { get; } = new(key, []);
    public override string Message => LocalizedMessage.Value;
}

public static class RemoteProtocol
{
    public const string SubProtocol = "yokowave.v2";
    public const int Version = 2;
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
