using YokoWave.Core.Localization;
namespace YokoWave.Core;

public enum DeviceKind { DualChannel, Vibration, Pump, Linear }
public enum WaveShape { Sine, Triangle, Pulse, Ramp, Custom }

public sealed record DeviceDescriptor(string Id, string Name, DeviceKind Kind, int Battery, int Signal)
{
    public string KindLabel => Kind switch
    {
        DeviceKind.DualChannel => L10n.Get("Device.DualChannel"),
        DeviceKind.Vibration => L10n.Get("Device.Vibration"),
        DeviceKind.Pump => L10n.Get("Device.Pump"),
        _ => L10n.Get("Device.Linear")
    };
}

public sealed record OutputFrame(double ChannelA, double ChannelB, double Frequency)
{
    public static OutputFrame Zero { get; } = new(0, 0, 1);
}

public sealed record WaveformPreset(
    Guid Id, string Name, WaveShape Shape, double PeriodSeconds,
    double Frequency, string Description, double[]? Points = null)
{
    public static IReadOnlyList<WaveformPreset> BuiltIn { get; } =
    [
        new(Guid.Parse("e3934206-0b7d-4e97-a3eb-3524fa772001"), L10n.Get("Wave.Sine.Name"), WaveShape.Sine, 4, 20, L10n.Get("Wave.Sine.Description")),
        new(Guid.Parse("e3934206-0b7d-4e97-a3eb-3524fa772002"), L10n.Get("Wave.Triangle.Name"), WaveShape.Triangle, 3, 30, L10n.Get("Wave.Triangle.Description")),
        new(Guid.Parse("e3934206-0b7d-4e97-a3eb-3524fa772003"), L10n.Get("Wave.Pulse.Name"), WaveShape.Pulse, 2, 40, L10n.Get("Wave.Pulse.Description")),
        new(Guid.Parse("e3934206-0b7d-4e97-a3eb-3524fa772004"), L10n.Get("Wave.Ramp.Name"), WaveShape.Ramp, 5, 25, L10n.Get("Wave.Ramp.Description"))
    ];

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
            throw new LocalizedArgumentException("Error.WaveName");
        if (!Enum.IsDefined(Shape) || !double.IsFinite(PeriodSeconds) || PeriodSeconds is < 0.2 or > 60)
            throw new LocalizedArgumentException("Error.Period");
        if (!double.IsFinite(Frequency) || Frequency is < 1 or > 100)
            throw new LocalizedArgumentException("Error.Frequency");
        if (Description is null || Description.Length > 300)
            throw new LocalizedArgumentException("Error.Description");
        if (Shape == WaveShape.Custom && (Points is null || Points.Length is < 2 or > 256 ||
            Points.Any(x => !double.IsFinite(x) || x is < 0 or > 1)))
            throw new LocalizedArgumentException("Error.Points");
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
            _ => 0
        };
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
    bool OffsetChannelB = true, string? Language = null);

public sealed record WorkspaceData(int SchemaVersion, List<WaveformPreset> Presets,
    List<SessionRecord> Sessions, AppSettings Settings)
{
    public static WorkspaceData Empty => new(1, [], [], new());
}
