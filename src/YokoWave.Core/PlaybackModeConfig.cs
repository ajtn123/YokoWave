using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed record PlaybackModeConfig(Guid Id, string Name, ModeKind Kind = ModeKind.Waveform,
    double ChannelA = 0, double ChannelB = 0, double OutputLimit = 60,
    bool TimerEnabled = true, int DurationSeconds = 600, Guid? WaveformAId = null, Guid? WaveformBId = null,
    EstimSettings? Estim = null, List<string>? TargetDeviceIds = null, bool OffsetChannelB = true,
    List<Guid>? PlaylistWaveformIds = null, AudioSettings? Audio = null, ReactiveSettings? Reactive = null,
    double ChannelC = 0, PumpOutput? Pump = null, DgBalanceSettings? DgBalances = null,
    NativeProgramSettings? NativeProgram = null, HardwareOutputSettings? Hardware = null)
{
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 60 || !Enum.IsDefined(Kind) ||
            new[] { ChannelA, ChannelB, ChannelC, OutputLimit }.Any(value => !double.IsFinite(value) || value is < 0 or > 100) ||
            ChannelA > OutputLimit || ChannelB > OutputLimit || ChannelC > OutputLimit || DurationSeconds is < 1 or > 7200 ||
            TargetDeviceIds is { } targets && (targets.Any(string.IsNullOrWhiteSpace) || targets.Distinct().Count() != targets.Count) ||
            PlaylistWaveformIds is { } playlist && (playlist.Count > 100 || playlist.Contains(Guid.Empty) || playlist.Distinct().Count() != playlist.Count))
            throw new LocalizedArgumentException("Error.ModeSettings");
        Estim?.Validate();
        Audio?.Validate();
        Reactive?.Validate();
        Hardware?.Validate();
        if (Kind == ModeKind.NativeProgram)
            new NativeProgramOutput(NativeProgram ?? new(), 0, null).ValidateOutput(new(ChannelA, ChannelB, 20, ChannelC: ChannelC, OutputLimit: OutputLimit, Hardware: Hardware));
        Pump?.Validate();
    }
}
