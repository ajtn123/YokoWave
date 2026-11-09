namespace YokoWave.Pipelines;

public sealed class PipelineStore
{
    private readonly JsonStateFile<PipelineDocument[]> _pipelines;
    private readonly JsonStateFile<PipelinePreset[]> _presets;
    public string FilePath { get; }
    public string PresetsPath { get; }
    public PipelineStore(string directory)
    {
        FilePath = Path.Combine(Path.GetFullPath(directory), "pipelines.json");
        PresetsPath = Path.Combine(Path.GetFullPath(directory), "pipeline-presets.json");
        _pipelines = new(FilePath, documents => documents.Length <= 32 && documents.All(d => d is not null) && documents.Select(d => d.Id).Distinct().Count() == documents.Length);
        _presets = new(PresetsPath, presets => presets.Length <= 100 && presets.All(p => p is not null && p.Graph is not null && !string.IsNullOrWhiteSpace(p.Name)) &&
            presets.Select(p => p.Id).Distinct().Count() == presets.Length);
    }
    public async Task<IReadOnlyList<PipelineDocument>> LoadAsync() => await _pipelines.LoadAsync([]);
    public Task SaveAsync(IEnumerable<PipelineDocument> documents) => _pipelines.SaveAsync(documents.ToArray());
    public async Task<IReadOnlyList<PipelinePreset>> LoadPresetsAsync() => await _presets.LoadAsync([]);
    public Task SavePresetsAsync(IEnumerable<PipelinePreset> presets) => _presets.SaveAsync(presets.ToArray());
}
