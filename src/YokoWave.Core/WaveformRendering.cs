using YokoWave.Core.Localization;

namespace YokoWave.Core;

/// <summary>Renders device-independent samples without changing the stored waveform or mode settings.</summary>
public static class WaveformRendering
{
    public static OutputFrame WithRemaining(OutputFrame frame, double remainingSeconds)
    {
        IReadOnlyList<WaveformSample> Clip(int channel) => Enumerable.Range(0, 4).Select(index =>
            index * .025 >= remainingSeconds - 1e-9 ? new WaveformSample(0, 1, 0) : Sample(frame, channel, index)).ToArray();
        return frame with { WindowA = Clip(0), WindowB = Clip(1), WindowC = Clip(2) };
    }
    public static WaveformSample Sample(OutputFrame frame, int channel, int index = 0)
    {
        var window = channel switch { 1 => frame.WindowB, 2 => frame.WindowC ?? frame.WindowA, _ => frame.WindowA };
        if (window is { Count: not 4 })
            throw new LocalizedArgumentException("Error.Samples");
        var sample = window is not null ? window[index] ?? throw new LocalizedArgumentException("Error.Samples")
            : channel switch { 1 => frame.ReferenceSampleB ?? frame.ReferenceSample, 2 => frame.ReferenceSampleC ?? frame.ReferenceSample, _ => frame.ReferenceSample };
        sample ??= new(1, double.IsFinite(frame.Frequency) ? Math.Clamp(frame.Frequency, 1, 100) : 1);
        if (!sample.IsValid)
            throw new LocalizedArgumentException("Error.Samples");
        return sample;
    }
    public static double PeakLevel(OutputFrame frame, int channel, bool window)
    {
        if (!window)
            return Sample(frame, channel).Level;
        return Enumerable.Range(0, 4).Select(index => Sample(frame, channel, index).Level).Max();
    }
    public static double Strength(OutputFrame frame, int channel, bool window = false)
    {
        if (!double.IsFinite(frame.OutputLimit) || frame.OutputLimit is < 0 or > 100)
            throw new LocalizedArgumentException("Error.InvalidSettings");
        double value = channel == 0 ? frame.ChannelA : channel == 1 ? frame.ChannelB : frame.ChannelC;
        return (double.IsFinite(value) ? Math.Clamp(value, 0, Math.Clamp(frame.OutputLimit, 0, 100)) : 0) * PeakLevel(frame, channel, window);
    }
    public static double Width(OutputFrame frame, int channel, int index = 0, bool window = false)
    {
        var sample = Sample(frame, channel, index);
        double peak = window ? PeakLevel(frame, channel, true) : sample.Level;
        return peak > 0 ? sample.PulseWidth * sample.ChannelMagnitude(channel) * (window ? sample.Level / peak : 1) : 0;
    }
}
