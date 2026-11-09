namespace YokoWave.Core;

public sealed record WorkspaceData(int SchemaVersion, List<WaveformAsset> Waveforms,
    List<SessionRecord> Sessions, AppSettings Settings, SessionStatistics? Statistics = null)
{
    public const int CurrentSchema = 11;
    public static WorkspaceData Empty => new(CurrentSchema, [], [], new(), new());
}
