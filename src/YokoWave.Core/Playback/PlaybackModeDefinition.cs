using YokoWave.Core.Localization;

namespace YokoWave.Core;

/// <summary>Shared capabilities used by validation, routing and the editor. Runtime behavior lives in the mode classes.</summary>
public sealed record PlaybackModeDefinition(ModeKind Kind, bool UsesWaveforms = false, bool SupportsPlaylist = false,
    bool UsesAudio = false, bool UsesReactiveSettings = false, bool IsPump = false, bool AlwaysNeedsMotion = false)
{
    public bool NeedsMotion(ReactiveSettings? settings) => AlwaysNeedsMotion ||
        Kind == ModeKind.SleepDeprivation && settings?.SleepCycle == SleepCycleKind.ActivityReset;

    public static PlaybackModeDefinition For(ModeKind kind) => Definitions.TryGetValue(kind, out var definition)
        ? definition : throw new LocalizedArgumentException("Error.ModeSettings");

    private static readonly IReadOnlyDictionary<ModeKind, PlaybackModeDefinition> Definitions = new Dictionary<ModeKind, PlaybackModeDefinition>
    {
        [ModeKind.Waveform] = new(ModeKind.Waveform, UsesWaveforms: true, SupportsPlaylist: true),
        [ModeKind.Touch] = new(ModeKind.Touch),
        [ModeKind.Audio] = new(ModeKind.Audio, UsesAudio: true),
        [ModeKind.Restraint] = new(ModeKind.Restraint, UsesReactiveSettings: true, AlwaysNeedsMotion: true),
        [ModeKind.Exercise] = new(ModeKind.Exercise, UsesWaveforms: true, UsesReactiveSettings: true, AlwaysNeedsMotion: true),
        [ModeKind.SleepDeprivation] = new(ModeKind.SleepDeprivation, UsesWaveforms: true, SupportsPlaylist: true, UsesReactiveSettings: true),
        [ModeKind.Pump] = new(ModeKind.Pump, IsPump: true),
        [ModeKind.NativeProgram] = new(ModeKind.NativeProgram),
        [ModeKind.WebSocketServer] = new(ModeKind.WebSocketServer)
    };
}
