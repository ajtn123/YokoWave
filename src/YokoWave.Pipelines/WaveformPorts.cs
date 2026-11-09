using YokoWave.Core;

namespace YokoWave.Pipelines;

/// <summary>Stable source-channel addresses allow live waveform changes without changing graph routing.</summary>
public static class WaveformPorts
{
    public static string Level(int channel) => "level" + (char)('A' + channel);
    public static NodePort[] Create(bool trigger = false) =>
        (trigger ? new[] { NodePort.Input("trigger", "Pipeline.Port.trigger", 0, 1) } : [])
        .Concat(Enumerable.Range(0, 3).Select(channel => NodePort.Output(Level(channel), "Pipeline.Port." + Level(channel), "ratio")))
        .Concat([NodePort.Output("frequency", "Pipeline.Port.Frequency", "Hz"), NodePort.Output("pulseWidth", "Pipeline.Port.PulseWidth", "ratio")]).ToArray();
    public static bool IsAvailable(NodePort port, WaveformPreset? waveform) =>
        !Enumerable.Range(0, 3).Any(channel => port.Id == Level(channel) && channel >= (waveform?.ChannelCount ?? 0));
    internal static void Write(NodeEvaluationContext context, WaveformPreset waveform, IReadOnlyList<WaveformSample> samples, bool completed = false)
    {
        var count = waveform.ChannelCount;
        for (var channel = 0; channel < 3; channel++)
            context.Output(Level(channel), new(samples.Select(sample => completed || channel >= count ? 0 : sample.Level * sample.ChannelMagnitude(channel)).ToArray(), DateTimeOffset.MaxValue));
        context.Output("frequency", new(samples.Select(sample => sample.FrequencyHz).ToArray(), DateTimeOffset.MaxValue));
        context.Output("pulseWidth", new(samples.Select(sample => sample.PulseWidth).ToArray(), DateTimeOffset.MaxValue));
    }
}
