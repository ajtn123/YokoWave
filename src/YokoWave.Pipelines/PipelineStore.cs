using System.Text.Json;
using YokoWave.Core.Localization;

namespace YokoWave.Pipelines;

public sealed class PipelineStore(string directory)
{
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory), "pipelines.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loadFailed;

    public async Task<IReadOnlyList<PipelineDocument>> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(FilePath))
                return [];
            var documents = JsonSerializer.Deserialize<PipelineDocument[]>(await File.ReadAllTextAsync(FilePath), PipelineJson.Options);
            if (documents is null || documents.Any(d => d is null) || documents.Select(d => d.Id).Distinct().Count() != documents.Length)
                throw new JsonException();
            return documents;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            throw new LocalizedIOException("Pipeline.Error.Storage", error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<PipelineDocument> documents)
    {
        var json = JsonSerializer.Serialize(documents.ToArray(), PipelineJson.Options);
        await _gate.WaitAsync();
        try
        {
            if (_loadFailed)
                throw new LocalizedOperationException("Pipeline.Error.Storage");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, FilePath, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
