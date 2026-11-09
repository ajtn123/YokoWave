namespace YokoWave.Tests.Support;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ArtifactDirectory(string name)
    {
        string path = Path.Combine(RepositoryRoot, "artifacts", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "YokoWave.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("The test assembly must run inside the YokoWave checkout.");
    }
}
