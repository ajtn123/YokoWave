using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed record OutputFrame(double ChannelA, double ChannelB, double Frequency,
    WaveformSample? ReferenceSample = null, WaveformSample? ReferenceSampleB = null, bool Gentle = false,
    double ChannelC = 0,
    IReadOnlyList<WaveformSample>? WindowA = null, IReadOnlyList<WaveformSample>? WindowB = null,
    double OutputLimit = 100, PumpOutput? Pump = null, DgBalanceSettings? DgBalances = null,
    NativeProgramOutput? NativeProgram = null, HardwareOutputSettings? Hardware = null,
    WaveformSample? ReferenceSampleC = null, IReadOnlyList<WaveformSample>? WindowC = null)
{
    public static OutputFrame Zero { get; } = new(0, 0, 1);
    public void Validate()
    {
        if (new[] { ChannelA, ChannelB, ChannelC, OutputLimit }.Any(value => !double.IsFinite(value) || value is < 0 or > 100) ||
            !double.IsFinite(Frequency) || Frequency is < 1 or > 100)
        {
            throw new LocalizedArgumentException("Error.InvalidSettings");
        }

        Hardware?.Validate();
        Pump?.Validate();
        NativeProgram?.ValidateOutput(this);
        ValidateSamples();
    }
    public void ValidateSamples()
    {
        foreach (var sample in new[] { ReferenceSample, ReferenceSampleB, ReferenceSampleC }.OfType<WaveformSample>())
        {
            if (!sample.IsValid)
                throw new LocalizedArgumentException("Error.Samples");
        }

        foreach (var window in new[] { WindowA, WindowB, WindowC }.OfType<IReadOnlyList<WaveformSample>>())
        {
            if (window.Count != 4 || window.Any(sample => sample is null || !sample.IsValid))
                throw new LocalizedArgumentException("Error.Samples");
        }
    }
}
