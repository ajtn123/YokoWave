using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using static YokoWave.Tests.Support.Pipelines.PipelineTestGraphs;

namespace YokoWave.Tests.Storage;

public sealed class PipelineStoreTests
{
    [Fact]
    public async Task SavedTemplatesUseTheWorkspaceManagedFileAndAreClearedWithLocalData()
    {
        using var folder = new TestFolder();
        var store = new PipelineStore(folder.Path);
        var graph = CreateGraph([CreateNumber(.3)]);
        var template = new SavedPipelineTemplate(Guid.NewGuid(), "saved", graph, [], []);
        await store.SaveTemplatesAsync([template]);

        Assert.Equal(Path.Combine(folder.Path, "pipeline-presets.json"), store.TemplatesPath);
        Assert.Equal(template.Id, Assert.Single(await store.LoadTemplatesAsync()).Id);
        await new WorkspaceStore(folder.Path).ClearAsync();
        Assert.False(File.Exists(store.TemplatesPath));
        Assert.Empty(await new PipelineStore(folder.Path).LoadTemplatesAsync());
    }

    [Fact]
    public async Task DocumentsRoundTripWithoutRuntimeStateAndCorruptionCannotBeOverwritten()
    {
        using var directory = new TestFolder();
        var store = new PipelineStore(directory.Path);
        var node = CreateNumber(.3);
        var graph = CreateGraph([node, CreateOutput()]) with
        {
            Layout = [new(node.Id, 123, 456)],
            DurationSeconds = 5
        };
        await store.SaveAsync([graph]);
        string json = await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain("PipelineSession", json);
        Assert.DoesNotContain("Nodify", json);
        Assert.Equal(JsonSerializer.Serialize(graph, PipelineJson.Options), JsonSerializer.Serialize(Assert.Single(await store.LoadAsync()), PipelineJson.Options));
        await File.WriteAllTextAsync(store.FilePath, "corrupt", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<LocalizedIOException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<LocalizedOperationException>(() => store.SaveAsync([]));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }
}
