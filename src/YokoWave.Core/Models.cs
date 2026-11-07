using YokoWave.Core.Localization;
namespace YokoWave.Core;

public enum DeviceKind { DualChannel, Vibration, Pump, Linear }
public enum WaveShape { Sine, Triangle, Pulse, Ramp, Custom, Sampled }
public enum WaveformFamily { Generic, Electrical, Motor }

public sealed record DeviceDescriptor(string Id, string Name, DeviceKind Kind, int? Battery, int? Signal)
{
    public string KindLabel => Kind switch
    {
        DeviceKind.DualChannel => L10n.Get("Device.DualChannel"),
        DeviceKind.Vibration => L10n.Get("Device.Vibration"),
        DeviceKind.Pump => L10n.Get("Device.Pump"),
        _ => L10n.Get("Device.Linear")
    };
}

public sealed record OutputFrame(double ChannelA, double ChannelB, double Frequency,
    WaveformSample? ReferenceSample = null, WaveformSample? ReferenceSampleB = null)
{
    public static OutputFrame Zero { get; } = new(0, 0, 1);
}

public sealed record WaveformSample(double Level, double FrequencyCode, double PulseWidthCode, double[]? Motors = null);

public sealed record WaveformPreset(
    Guid Id, string Name, WaveShape Shape, double PeriodSeconds,
    double Frequency, double[]? Points = null, WaveformSample[]? Samples = null,
    WaveformFamily Family = WaveformFamily.Generic)
{
    public static IReadOnlyList<WaveformPreset> BuiltIn => DefaultWaveformCatalog.Presets;

    public void Validate()
    {
        if (!TryValidate(out var errorKey)) throw new LocalizedArgumentException(errorKey!);
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
        if (!Enum.IsDefined(Family)) return "Error.InvalidWave";
        if (!Enum.IsDefined(Shape) || !double.IsFinite(PeriodSeconds) || PeriodSeconds is < 0.2 or > 60)
            return "Error.Period";
        if (!double.IsFinite(Frequency) || Frequency is < 1 or > 100)
            return "Error.Frequency";
        if (Shape == WaveShape.Custom && (Points is null || Points.Length is < 2 or > 256 ||
            Points.Any(x => !double.IsFinite(x) || x is < 0 or > 1)))
            return "Error.Points";
        if (Shape == WaveShape.Sampled && (Samples is null || Samples.Length is < 2 or > 256 || Samples.Any(sample =>
            sample is null || !double.IsFinite(sample.Level) || sample.Level is < 0 or > 1 ||
            !double.IsFinite(sample.FrequencyCode) || sample.FrequencyCode is < 0 or > 100 ||
            !double.IsFinite(sample.PulseWidthCode) || sample.PulseWidthCode is < 0 or > 100 ||
            sample.Motors is { } motors && (motors.Length != 3 || motors.Any(x => !double.IsFinite(x) || x is < -20 or > 20)))))
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
            WaveShape.Sampled => ReferenceSample(preset, seconds, phase)?.Level ?? 0,
            _ => 0
        };
    }

    public static WaveformSample? ReferenceSample(WaveformPreset preset, double seconds, double phase = 0)
    {
        if (preset.Shape != WaveShape.Sampled || preset.Samples is not { Length: >= 2 } samples) return null;
        var position = Math.Round(((seconds / preset.PeriodSeconds + phase) % 1 + 1) % 1, 12) % 1;
        var index = (int)Math.Floor(Math.Round(position * samples.Length, 9));
        return samples[Math.Min(index, samples.Length - 1)];
    }

    private static double Interpolate(double[] points, double position)
    {
        var index = position * (points.Length - 1);
        var left = (int)index;
        var right = Math.Min(left + 1, points.Length - 1);
        return points[left] + (points[right] - points[left]) * (index - left);
    }
}

public sealed record SessionRecord(Guid Id, DateTimeOffset StartedAt, double DurationSeconds,
    string WaveformName, int DeviceCount, double PeakA, double PeakB, string Reason, Guid? WaveformId = null);

public sealed record AppSettings(double DefaultLimit = 60, double SessionMinutes = 10,
    bool OffsetChannelB = true, string? Language = null, bool UseBluetooth = false);

public sealed record WorkspaceData(int SchemaVersion, List<WaveformPreset> Presets,
    List<SessionRecord> Sessions, AppSettings Settings)
{
    public static WorkspaceData Empty => new(1, [], [], new());
}
