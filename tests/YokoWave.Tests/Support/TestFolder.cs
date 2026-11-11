using YokoWave.Core;

namespace YokoWave.Tests.Support;

internal sealed class TestFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(TestPaths.ArtifactDirectory("test-data"), Guid.NewGuid().ToString("N"));

    public TestFolder() => Directory.CreateDirectory(Path);
    public async Task<WorkspaceStore> CreateSimulationStoreAsync()
    {
        var store = new WorkspaceStore(Path);
        if (!File.Exists(store.SettingsPath))
            await store.SaveAsync(WorkspaceData.Empty with { Settings = new(UseBluetooth: false) });
        return store;
    }
    public void Dispose() => Directory.Delete(Path, true);
}
