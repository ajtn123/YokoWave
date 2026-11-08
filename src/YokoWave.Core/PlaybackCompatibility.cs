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

}
