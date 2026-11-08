namespace YokoWave.Core;

internal readonly record struct PlaybackContext(string DeviceId, TimeSpan Elapsed, MotionReading? Motion,
    bool OffsetChannelB, double? RemainingSeconds, PumpOutput Pump);

/// <summary>A fresh instance owns all mode state for one session. The engine owns device leases and serialization.</summary>
internal abstract class PlaybackModeRuntime
{
    public virtual WaveformPlayback? Waveforms => null;
    public virtual IReadOnlyDictionary<string, PlaybackInputStatus> Status => EmptyStatus;
    private static readonly IReadOnlyDictionary<string, PlaybackInputStatus> EmptyStatus = new Dictionary<string, PlaybackInputStatus>();
    public virtual void Validate()
    {
    }
    public virtual string? Advance(TimeSpan elapsed) => null;
    public virtual string? CompletionReason(int targetCount) => null;
    public virtual void Suspend()
    {
    }
    public abstract OutputFrame Render(PlaybackContext context, OutputFrame frame);

    public static PlaybackModeRuntime Create(SessionProgram program, WaveformPreset preset, TimeProvider clock, Random random) => program.Kind switch
    {
        ModeKind.Waveform => new WaveformModeRuntime(program, preset, random),
        ModeKind.Touch => new TouchModeRuntime((program.Settings ?? new()).TouchPlayback),
        ModeKind.Audio => new AudioModeRuntime(program.Audio ?? new(), clock),
        ModeKind.Restraint => new RestraintModeRuntime(program.Reactive ?? new(), clock),
        ModeKind.Exercise => new ExerciseModeRuntime(program, preset, clock, random),
        ModeKind.SleepDeprivation => new SleepModeRuntime(program, preset, clock, random),
        ModeKind.Pump => new PumpModeRuntime(),
        ModeKind.NativeProgram => new NativeProgramModeRuntime(program.NativeProgram ?? new()),
        _ => throw new Localization.LocalizedArgumentException("Error.ModeSettings")
    };
}

internal interface IAudioPlaybackInput
{
    void SetAudio(AudioFeatureFrame? frame);
}

internal interface ITouchPlaybackInput
{
    IReadOnlyList<WaveformSample> Recording
    { get; }
    void SetTouch(double frequency, double pulseWidth, bool held, double elapsed);
}

internal interface IMotionPlaybackInput
{
    int MovementCount
    { get; }
    void Recalibrate(double elapsed);
}
