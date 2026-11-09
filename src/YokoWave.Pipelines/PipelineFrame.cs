using YokoWave.Core;

namespace YokoWave.Pipelines;

public sealed record PipelineFrame(IReadOnlyDictionary<string, OutputFrame> Outputs, IReadOnlyList<PipelineDiagnostic> Diagnostics,
    IReadOnlyList<PipelineCommandIntent>? Commands = null);
