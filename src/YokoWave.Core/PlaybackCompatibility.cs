namespace YokoWave.Core;

public static class PlaybackCompatibility
{
    public static bool CanRun(DeviceDescriptor device, ModeKind kind, ReactiveSettings? reactive = null)
    {
        if (kind == ModeKind.WebSocketServer)
            return device.Origin == DeviceOrigin.Local && device.Simulated != true && device.Profile is { Supported: true };
        if (!device.IsSupported || device.IsInput) return false;
        var definition = PlaybackModeDefinition.For(kind);
        if ((device.Kind == DeviceKind.Pump) != definition.IsPump) return false;
        return !definition.NeedsMotion(reactive) || device.Profile is null || device.Profile.Motion;
    }

    public static bool Accepts(DeviceDescriptor device, WaveformPreset preset) => device.Profile?.Accepts(preset) ??
        (device.Kind != DeviceKind.Pump && !device.IsInput);

}
