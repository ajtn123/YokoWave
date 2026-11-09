namespace YokoWave.Core;

public sealed record SessionRecord(Guid Id, DateTimeOffset StartedAt, double DurationSeconds,
    string PipelineName, int DeviceCount, double? PeakA, double? PeakB, string Reason, Guid? PipelineId = null,
    double? PeakC = null, IReadOnlyList<PipelineContentRecord>? Contents = null);
