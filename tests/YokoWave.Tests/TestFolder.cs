namespace YokoWave.Tests;

internal sealed class TestFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
        "../../../../../artifacts/test-data", Guid.NewGuid().ToString("N")));
    public TestFolder() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
