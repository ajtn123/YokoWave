using YokoWave.Core;

namespace YokoWave.Tests.Storage;

public sealed class SessionJournalTests
{
    [Fact]
    public async Task JournalRollsAtUtf8ByteLimitKeepsOnePreviousFileAndPreservesTotals()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var records = Enumerable.Range(0, 16).Select(i => Record(i, new string('字', 6000))).ToList();
        var data = new WorkspaceData(WorkspaceData.CurrentSchema, [], records, new());
        await store.SaveAsync(data);
        Assert.Equal(2, Directory.GetFiles(folder.Path, "*.jsonl").Length);
        Assert.InRange(new FileInfo(store.Journal.FilePath).Length, 1, SessionJournal.MaxFileBytes);
        Assert.InRange(new FileInfo(store.Journal.PreviousFilePath).Length, 1, SessionJournal.MaxFileBytes);
        byte[] before = await File.ReadAllBytesAsync(store.Journal.FilePath, TestContext.Current.CancellationToken);
        await store.SaveAsync(data);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.Journal.FilePath, TestContext.Current.CancellationToken));
        var restored = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.InRange(restored.Sessions.Count, 1, 15);
        Assert.Contains(restored.Sessions, record => record.Id == records[^1].Id);
        Assert.Equal(new SessionStatistics(16, 160), restored.Statistics);
    }

    [Fact]
    public async Task ConcurrentSavesDoNotDuplicateSessionLinesOrStatistics()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var records = Enumerable.Range(0, 8).Select(i => Record(i)).ToList();
        var data = new WorkspaceData(WorkspaceData.CurrentSchema, [], records, new());
        await Task.WhenAll(store.SaveAsync(data), store.SaveAsync(data));
        Assert.Equal(8, (await File.ReadAllLinesAsync(store.Journal.FilePath, TestContext.Current.CancellationToken)).Length);
        Assert.Equal(new SessionStatistics(8, 80), (await store.LoadAsync()).Statistics);
    }

    [Fact]
    public async Task CorruptSessionLogIsPreservedAndBlocksSaving()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        await File.WriteAllTextAsync(store.Journal.FilePath, "invalid\n", TestContext.Current.CancellationToken);
        Assert.Empty((await store.LoadAsync()).Sessions);
        Assert.NotNull(store.LoadWarning);
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(WorkspaceData.Empty));
        Assert.Equal("invalid\n", await File.ReadAllTextAsync(store.Journal.FilePath, TestContext.Current.CancellationToken));
    }

    private static SessionRecord Record(int index, string? reason = null) => new(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(index), 10, "波形", 1, 20, 10, reason ?? "Reason.Manual");
}
