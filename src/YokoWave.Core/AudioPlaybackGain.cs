namespace YokoWave.Core;

/// <summary>Final speaker gain, applied after feature extraction. Zero clears every output sample.</summary>
public sealed class AudioPlaybackGain(double volumePercent)
{
    private readonly float _gain = double.IsFinite(volumePercent) ? (float)(Math.Clamp(volumePercent, 0, 100) / 100) : 0;
    public float Apply(float sample) => _gain == 0 || !float.IsFinite(sample) ? 0 : sample * _gain;
    public void Apply(Span<float> samples)
    {
        if (_gain == 0)
        {
            samples.Clear();
            return;
        }
        for (int index = 0; index < samples.Length; index++)
            samples[index] = Apply(samples[index]);
    }
}
