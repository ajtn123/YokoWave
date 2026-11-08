namespace YokoWave.Core;

internal sealed class PumpModeRuntime : PlaybackModeRuntime
{
    public override OutputFrame Render(PlaybackContext context, OutputFrame frame) => frame with { Pump = context.Pump };
}
