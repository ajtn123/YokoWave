namespace YokoWave.Core;

public enum DeviceKind { DualChannel, Vibration, Pump, Linear }
public enum WaveShape { Sine, Triangle, Pulse, Ramp, Custom }

public sealed record DeviceDescriptor(string Id, string Name, DeviceKind Kind, int Battery, int Signal)
{
    public string KindLabel => Kind switch
    {
        DeviceKind.DualChannel => "双通道设备",
        DeviceKind.Vibration => "振动设备",
        DeviceKind.Pump => "气泵设备",
        _ => "线性设备"
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
        new(Guid.Parse("e3934206-0b7d-4e97-a3eb-3524fa772001"), "轻柔呼吸", WaveShape.Sine, 4, 20, "平滑起伏，缓缓进入节奏"),
        new(Guid.Parse("e3934206-0b7d-4e97-a3eb-3524fa772002"), "三角律动", WaveShape.Triangle, 3, 30, "匀速上升与回落"),
        new(Guid.Parse("e3934206-0b7d-4e97-a3eb-3524fa772003"), "间歇脉冲", WaveShape.Pulse, 2, 40, "清晰的间歇节奏"),
        new(Guid.Parse("e3934206-0b7d-4e97-a3eb-3524fa772004"), "渐进阶梯", WaveShape.Ramp, 5, 25, "逐步累积，循环释放")
    ];

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
            throw new ArgumentException("波形需要有效的标识和 1–60 字名称。");
        if (!Enum.IsDefined(Shape) || !double.IsFinite(PeriodSeconds) || PeriodSeconds is < 0.2 or > 60)
            throw new ArgumentException("周期应在 0.2–60 秒之间。");
        if (!double.IsFinite(Frequency) || Frequency is < 1 or > 100)
            throw new ArgumentException("频率应在 1–100 Hz 之间。");
        if (Description is null || Description.Length > 300)
            throw new ArgumentException("描述应在 300 字以内。");
        if (Shape == WaveShape.Custom && (Points is null || Points.Length is < 2 or > 256 ||
            Points.Any(x => !double.IsFinite(x) || x is < 0 or > 1)))
            throw new ArgumentException("自定义波形需要 2–256 个 0–1 之间的采样点。");
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
    string WaveformName, int DeviceCount, double PeakA, double PeakB, string Reason);

public sealed record AppSettings(double DefaultLimit = 60, double SessionMinutes = 10,
    bool OffsetChannelB = true);

public sealed record WorkspaceData(int SchemaVersion, List<WaveformPreset> Presets,
    List<SessionRecord> Sessions, AppSettings Settings)
{
    public static WorkspaceData Empty => new(1, [], [], new());
}
