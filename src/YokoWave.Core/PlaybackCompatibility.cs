namespace YokoWave.Core;

public static class PlaybackCompatibility
{
    public static bool CanRun(DeviceDescriptor device, ModeKind kind, ReactiveSettings? reactive = null)
    {
        if (!device.IsSupported || device.IsInput) return false;
        if ((device.Kind == DeviceKind.Pump) != (kind == ModeKind.Pump)) return false;
        var needsMotion = kind is ModeKind.Restraint or ModeKind.Exercise ||
            kind == ModeKind.SleepDeprivation && reactive?.SleepCycle == SleepCycleKind.ActivityReset;
        return !needsMotion || device.Profile is null || device.Profile.Motion;
    }

    public static bool Accepts(DeviceDescriptor device, WaveformPreset preset) => device.Profile?.Accepts(preset) ??
        (device.Kind != DeviceKind.Pump && !device.IsInput);

    public static IReadOnlyList<WaveformSample>? Window(WaveformPreset preset, double seconds, double phase = 0) =>
        preset.Family == WaveformFamily.DgLab
            ? Enumerable.Range(0, 4).Select(index => WaveformGenerator.ReferenceSample(preset, seconds + index * .025, phase)!).ToArray() : null;
}
