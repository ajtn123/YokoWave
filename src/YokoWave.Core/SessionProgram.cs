using YokoWave.Core.Localization;

namespace YokoWave.Core;

public enum PlaybackOrder { Repeat, Once, Sequential, Random }
public enum StrengthMode { Fixed, RandomIncrease, StepIncrease }
public enum TouchPlayback { Momentary, RecordedLoop, HoldLast }

public sealed record StrengthSettings(StrengthMode Mode = StrengthMode.Fixed, double IntervalSeconds = 10,
    double RandomAddition = 10, double Step = 1, double Ceiling = 60, double Boost = 10)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || !double.IsFinite(IntervalSeconds) || IntervalSeconds is < 1 or > 600 ||
            new[] { RandomAddition, Step, Ceiling, Boost }.Any(value => !double.IsFinite(value) || value is < 0 or > 100))
            throw new LocalizedArgumentException("Error.StrengthSettings");
    }
}

public sealed record EstimSettings(PlaybackOrder Order = PlaybackOrder.Repeat, StrengthSettings? Strength = null,
    bool SeparateWaveforms = false, bool Gentle = false, bool ShowContacts = true, TouchPlayback TouchPlayback = TouchPlayback.Momentary)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Order) || !Enum.IsDefined(TouchPlayback)) throw new LocalizedArgumentException("Error.PlaybackSettings");
        (Strength ?? new()).Validate();
    }
}

public sealed record SessionProgram(EstimSettings? Settings = null, IReadOnlyList<WaveformPreset>? Playlist = null,
    WaveformPreset? ChannelBWaveform = null, ModeKind Kind = ModeKind.Waveform, double OutputLimit = 60,
    AudioSettings? Audio = null, ReactiveSettings? Reactive = null, PumpOutput? Pump = null, DgBalanceSettings? DgBalances = null,
    NativeProgramSettings? NativeProgram = null, HardwareOutputSettings? Hardware = null)
{
    public bool TouchControl => Kind == ModeKind.Touch;
    public PlaybackModeDefinition Definition => PlaybackModeDefinition.For(Kind);
    public bool NeedsMotion => Definition.NeedsMotion(Reactive);

    public void Validate()
    {
        _ = Definition;
        (Settings ?? new()).Validate();
        Audio?.Validate();
        Reactive?.Validate();
        Hardware?.Validate();
        if (Kind == ModeKind.NativeProgram)
            new NativeProgramOutput(NativeProgram ?? new(), 0, null).ValidateOutput(new(0, 0, 20, OutputLimit: OutputLimit, Hardware: Hardware));
        if (!double.IsFinite(OutputLimit) || OutputLimit is < 0 or > 100) throw new LocalizedArgumentException("Error.InvalidSettings");
        ChannelBWaveform?.Validate();
        foreach (var wave in Playlist ?? []) wave.Validate();
        Pump?.Validate();
    }
}
