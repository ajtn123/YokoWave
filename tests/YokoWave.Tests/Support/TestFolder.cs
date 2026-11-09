namespace YokoWave.Tests.Support;

internal sealed class TestFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(TestPaths.ArtifactDirectory("test-data"), Guid.NewGuid().ToString("N"));

    public TestFolder() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
