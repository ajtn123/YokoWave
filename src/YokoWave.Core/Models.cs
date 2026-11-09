using YokoWave.Core.Localization;
using System.Text.Json.Serialization;
using System.Numerics;
namespace YokoWave.Core;

public enum DeviceKind
{
    DualChannel, Vibration, Pump, Linear, Sensor
}
public enum WaveShape
{
    Sine, Triangle, Pulse, Ramp, Custom, Sampled
}

public enum DeviceOrigin
{
    Local, Remote
}
public sealed record DeviceDescriptor(string Id, string Name, DeviceKind Kind, int? Battery, int? Signal,
    string? ProtocolId = null, int? ChannelMask = null, DeviceOrigin Origin = DeviceOrigin.Local, bool? Simulated = null)
{
    public Bluetooth.BluetoothDeviceProfile? Profile => Bluetooth.BluetoothDeviceProfiles.FromId(ProtocolId);
    public bool IsInput => Kind == DeviceKind.Sensor;
    public bool IsSupported => Profile?.Supported ?? true;
    public int ChannelCount => ChannelMask is { } mask ? BitOperations.PopCount((uint)(mask & 7))
        : Profile?.Channels ?? (Kind == DeviceKind.DualChannel ? 2 : IsInput ? 0 : 1);
    public bool HasChannel(int index) => index is >= 0 and < 3 && (ChannelMask is { } mask ? (mask & (1 << index)) != 0 : index < ChannelCount);
    public string KindLabel => Origin == DeviceOrigin.Remote ? L10n.Format("Remote.DeviceKind", LocalKindLabel) : LocalKindLabel;
    private string LocalKindLabel => Profile is { } profile ? L10n.Get(profile.NameKey) : Kind switch
    {
        DeviceKind.DualChannel => L10n.Get("Device.DualChannel"),
        DeviceKind.Vibration => L10n.Get("Device.Vibration"),
        DeviceKind.Pump => L10n.Get("Device.Pump"),
        DeviceKind.Sensor => L10n.Get("Device.Sensor"),
        _ => L10n.Get("Device.Linear")
    };
}

public sealed record OutputFrame(double ChannelA, double ChannelB, double Frequency,
    WaveformSample? ReferenceSample = null, WaveformSample? ReferenceSampleB = null, bool Gentle = false,
    double ChannelC = 0,
    IReadOnlyList<WaveformSample>? WindowA = null, IReadOnlyList<WaveformSample>? WindowB = null,
    double OutputLimit = 100, PumpOutput? Pump = null, DgBalanceSettings? DgBalances = null,
    NativeProgramOutput? NativeProgram = null, HardwareOutputSettings? Hardware = null,
    WaveformSample? ReferenceSampleC = null, IReadOnlyList<WaveformSample>? WindowC = null)
{
    public static OutputFrame Zero { get; } = new(0, 0, 1);
}

public sealed record WaveformSample([property: JsonRequired] double Level, [property: JsonRequired] double FrequencyHz,
    [property: JsonRequired] double PulseWidth = 1, double[]? Channels = null)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(Level) && Level is >= 0 and <= 1 && double.IsFinite(FrequencyHz) && FrequencyHz is >= 1 and <= 100 &&
        double.IsFinite(PulseWidth) && PulseWidth is >= 0 and <= 1 &&
        (Channels is null || Channels.Length is >= 1 and <= 3 && Channels.All(value => double.IsFinite(value) && value is >= -1 and <= 1));
    public double ChannelMagnitude(int channel) => Channels is null ? 1 : channel < Channels.Length ? Math.Abs(Channels[channel]) : 0;
    [JsonIgnore] public double PreviewLevel => Level * PulseWidth * (Channels?.Select(Math.Abs).Max() ?? 1);
}

public sealed record WaveformPreset(
    [property: JsonRequired] Guid Id, [property: JsonRequired] string Name, [property: JsonRequired] WaveShape Shape, [property: JsonRequired] double PeriodSeconds,
    [property: JsonRequired] double Frequency, double[]? Points = null, WaveformSample[]? Samples = null,
    [property: JsonRequired] int FormatVersion = 1)
{
    public static IReadOnlyList<WaveformPreset> BuiltIn => DefaultWaveformCatalog.Presets;
    [JsonIgnore]
    public int ChannelCount => Shape == WaveShape.Sampled && Samples is { Length: > 0 }
        ? Samples.Max(sample => sample.Channels?.Length ?? 1) : 1;

    public void Validate()
    {
        if (!TryValidate(out var errorKey))
            throw new LocalizedArgumentException(errorKey!);
    }

    public bool TryValidate(out string? errorKey)
    {
        errorKey = GetValidationError();
        return errorKey is null;
    }

    private string? GetValidationError()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
            return "Error.WaveName";
        if (FormatVersion != 1)
            return "Error.UnsupportedWaveFormat";
        if (!Enum.IsDefined(Shape) || !double.IsFinite(PeriodSeconds) || PeriodSeconds is < 0.2 or > 60)
            return "Error.Period";
        if (!double.IsFinite(Frequency) || Frequency is < 1 or > 100)
            return "Error.Frequency";
        if (Shape == WaveShape.Custom && (Points is null || Points.Length is < 2 or > 256 ||
            Points.Any(x => !double.IsFinite(x) || x is < 0 or > 1)))
            return "Error.Points";
        if (Shape == WaveShape.Sampled && (Samples is null || Samples.Length is < 2 or > 256 || Samples.Any(sample =>
            sample is null || !sample.IsValid)))
            return "Error.Samples";
        return null;
    }
}

public static class WaveformGenerator
{
    public static double Sample(WaveformPreset preset, double seconds, double phase = 0)
    {
        // Stable boundaries for discontinuous waves, including negative preview times.
        var position = Math.Round(((seconds / preset.PeriodSeconds + phase) % 1 + 1) % 1, 12) % 1;
        return preset.Shape switch
        {
            WaveShape.Sine => (1 - Math.Cos(position * Math.Tau)) / 2,
            WaveShape.Triangle => 1 - Math.Abs(2 * position - 1),
            WaveShape.Pulse => position < 0.4 ? 1 : 0,
            WaveShape.Ramp => Math.Floor(position * 5) / 4,
            WaveShape.Custom when preset.Points is { Length: >= 2 } points => Interpolate(points, position),
            WaveShape.Sampled => ReferenceSample(preset, seconds, phase)?.PreviewLevel ?? 0,
            _ => 0
        };
    }

    public static WaveformSample? ReferenceSample(WaveformPreset preset, double seconds, double phase = 0)
    {
        if (preset.Shape != WaveShape.Sampled || preset.Samples is not { Length: >= 2 } samples)
            return null;
        var position = Math.Round(((seconds / preset.PeriodSeconds + phase) % 1 + 1) % 1, 12) % 1;
        var index = (int)Math.Floor(Math.Round(position * samples.Length, 9));
        return samples[Math.Min(index, samples.Length - 1)];
    }

    public static WaveformSample At(WaveformPreset preset, double seconds, double phase = 0) =>
        ReferenceSample(preset, seconds, phase) ?? new(Sample(preset, seconds, phase), preset.Frequency);

    public static IReadOnlyList<WaveformSample> Window(WaveformPreset preset, double seconds, double phase = 0,
        bool repeat = true, double? remainingSeconds = null) => Enumerable.Range(0, 4).Select(index =>
        (!repeat && seconds + index * .025 >= preset.PeriodSeconds - 1e-9) ||
        remainingSeconds is { } remaining && index * .025 >= remaining - 1e-9
            ? new WaveformSample(0, 1, 0) : At(preset, seconds + index * .025, phase)).ToArray();

    private static double Interpolate(double[] points, double position)
    {
        var index = position * (points.Length - 1);
        var left = (int)index;
        var right = Math.Min(left + 1, points.Length - 1);
        return points[left] + (points[right] - points[left]) * (index - left);
    }
}

public sealed record PipelineContentRecord(Guid NodeId, string Type, Guid? WaveformId = null, string? WaveformName = null);
public sealed record SessionRecord(Guid Id, DateTimeOffset StartedAt, double DurationSeconds,
    string PipelineName, int DeviceCount, double? PeakA, double? PeakB, string Reason, Guid? PipelineId = null,
    double? PeakC = null, IReadOnlyList<PipelineContentRecord>? Contents = null);

public sealed record AppSettings(double DefaultLimit = 60, string? Language = null, bool UseBluetooth = false, int TimerLimitMinutes = 120,
    List<RemoteEndpointSettings>? RemoteEndpoints = null, bool ShowContacts = true, bool AutoConnect = false, List<string>? AutoConnectDevices = null,
    List<DeviceOutputSettings>? DeviceOutputs = null);

public sealed record WorkspaceData(int SchemaVersion, List<WaveformPreset> Presets,
    List<SessionRecord> Sessions, AppSettings Settings, SessionStatistics? Statistics = null)
{
    public const int CurrentSchema = 11;
    public static WorkspaceData Empty => new(CurrentSchema, [], [], new(), new());
}

public sealed record SessionStatistics(long SessionCount = 0, double TotalDurationSeconds = 0)
{
    public SessionStatistics Add(SessionRecord session) => new(SessionCount + 1, TotalDurationSeconds + session.DurationSeconds);
}
