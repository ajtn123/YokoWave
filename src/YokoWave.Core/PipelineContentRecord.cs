namespace YokoWave.Core;

public sealed record PipelineContentRecord(Guid NodeId, string Type, Guid? WaveformId = null, string? WaveformName = null);
