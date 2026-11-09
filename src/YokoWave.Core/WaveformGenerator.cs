namespace YokoWave.Core;

public static class WaveformGenerator
{
    public static double Sample(WaveformAsset waveform, double seconds, double phase = 0)
    {
        // Stable boundaries for discontinuous waves, including negative preview times.
        double position = Math.Round(((seconds / waveform.PeriodSeconds + phase) % 1 + 1) % 1, 12) % 1;
        return waveform.Shape switch
        {
            WaveShape.Sine => (1 - Math.Cos(position * Math.Tau)) / 2,
            WaveShape.Triangle => 1 - Math.Abs(2 * position - 1),
            WaveShape.Pulse => position < 0.4 ? 1 : 0,
            WaveShape.Ramp => Math.Floor(position * 5) / 4,
            WaveShape.Custom when waveform.Points is { Length: >= 2 } points => Interpolate(points, position),
            WaveShape.Sampled => ReferenceSample(waveform, seconds, phase)?.PreviewLevel ?? 0,
            _ => 0
        };
    }

    public static WaveformSample? ReferenceSample(WaveformAsset waveform, double seconds, double phase = 0)
    {
        if (waveform.Shape != WaveShape.Sampled || waveform.Samples is not { Length: >= 2 } samples)
            return null;
        double position = Math.Round(((seconds / waveform.PeriodSeconds + phase) % 1 + 1) % 1, 12) % 1;
        int index = (int)Math.Floor(Math.Round(position * samples.Length, 9));
        return samples[Math.Min(index, samples.Length - 1)];
    }

    public static WaveformSample At(WaveformAsset waveform, double seconds, double phase = 0) =>
        ReferenceSample(waveform, seconds, phase) ?? new(Sample(waveform, seconds, phase), waveform.Frequency);

    public static IReadOnlyList<WaveformSample> Window(WaveformAsset waveform, double seconds, double phase = 0,
        bool repeat = true, double? remainingSeconds = null) => Enumerable.Range(0, 4).Select(index =>
        (!repeat && seconds + index * .025 >= waveform.PeriodSeconds - 1e-9) ||
        remainingSeconds is { } remaining && index * .025 >= remaining - 1e-9
            ? new WaveformSample(0, 1, 0) : At(waveform, seconds + index * .025, phase)).ToArray();

    private static double Interpolate(double[] points, double position)
    {
        double index = position * (points.Length - 1);
        int left = (int)index;
        int right = Math.Min(left + 1, points.Length - 1);
        return points[left] + (points[right] - points[left]) * (index - left);
    }
}
