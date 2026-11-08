namespace YokoWave.Core;

internal sealed class WaveformModeRuntime(SessionProgram program, WaveformPreset preset, Random random) : PlaybackModeRuntime
{
    public override WaveformPlayback Waveforms { get; } = new(program, preset, random, (program.Settings ?? new()).Order);
    public override string? Advance(TimeSpan elapsed) => Waveforms.Advance(elapsed) ? "Reason.WaveComplete" : null;
    public override OutputFrame Render(PlaybackContext context, OutputFrame frame) => Waveforms.Render(context, frame);
}
