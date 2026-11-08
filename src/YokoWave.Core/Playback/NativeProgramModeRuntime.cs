namespace YokoWave.Core;

internal sealed class NativeProgramModeRuntime(NativeProgramSettings settings) : PlaybackModeRuntime
{
    public override void Validate() => settings.Validate();
    public override string? Advance(TimeSpan elapsed) => settings is { Kind: NativeProgramKind.ElectricalBuffered, Repeat: false } &&
        elapsed.TotalSeconds >= Math.Max(settings.BufferA!.Count, settings.BufferB!.Count) / 10d ? "Reason.WaveComplete" : null;
    public override OutputFrame Render(PlaybackContext context, OutputFrame frame)
    {
        var presets = settings.Kind is NativeProgramKind.ElectricalPreset or NativeProgramKind.MotorPreset;
        return frame with
        {
            ChannelA = presets && settings.PresetA == 0 ? 0 : frame.ChannelA,
            ChannelB = presets && settings.PresetB == 0 ? 0 : frame.ChannelB,
            ChannelC = settings.Kind == NativeProgramKind.MotorPreset && settings.PresetC > 0 ? frame.ChannelC : 0,
            NativeProgram = new(settings, context.Elapsed.TotalSeconds, context.RemainingSeconds)
        };
    }
}
