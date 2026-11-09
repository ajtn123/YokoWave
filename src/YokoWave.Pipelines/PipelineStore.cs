namespace YokoWave.Pipelines;

public sealed class PipelineStore
{
    private readonly JsonStateFile<PipelineDocument[]> _pipelines;
    private readonly JsonStateFile<SavedPipelineTemplate[]> _templates;
    public string FilePath { get; }
    public string TemplatesPath { get; }
    public PipelineStore(string directory)
    {
        FilePath = Path.Combine(Path.GetFullPath(directory), "pipelines.json");
        TemplatesPath = Path.Combine(Path.GetFullPath(directory), "pipeline-presets.json");
        _pipelines = new(FilePath, documents => documents.Length <= 32 && documents.All(d => d is not null) && documents.Select(d => d.Id).Distinct().Count() == documents.Length);
        _templates = new(TemplatesPath, templates => templates.Length <= 100 && templates.All(p => p is not null && p.Graph is not null && !string.IsNullOrWhiteSpace(p.Name)) &&
            templates.Select(p => p.Id).Distinct().Count() == templates.Length);
    }
    public async Task<IReadOnlyList<PipelineDocument>> LoadAsync() => await _pipelines.LoadAsync([]).ConfigureAwait(false);
    public Task SaveAsync(IEnumerable<PipelineDocument> documents) => _pipelines.SaveAsync(documents.ToArray());
    public async Task<IReadOnlyList<SavedPipelineTemplate>> LoadTemplatesAsync() => await _templates.LoadAsync([]).ConfigureAwait(false);
    public Task SaveTemplatesAsync(IEnumerable<SavedPipelineTemplate> templates) => _templates.SaveAsync(templates.ToArray());
}
