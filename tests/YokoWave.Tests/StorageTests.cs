using System.Text.Json;
using YokoWave.Core;
using Xunit;

namespace YokoWave.Tests;

public sealed class StorageTests
{
    private static SessionRecord Record(int index, string? reason = null) => new(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(index),
        10, "波形", 1, 20, 10, reason ?? "Reason.Manual");

    [Fact]
    public async Task WorkspaceContainsOnlyStatisticsAndStorageUsesCompactSeparateFiles()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var preset = DefaultWaveformCatalog.Presets[0] with { Id = Guid.NewGuid(), Name = "custom" };
        var record = Record(1);
        var settings = new AppSettings(TimerLimitMinutes: 5);
        var mode = new PlaybackModeConfig(Guid.NewGuid(), "Mode", DurationSeconds: 3);
        await store.SaveAsync(new(3, [preset], [record], settings, Modes: [mode]));
        var json = await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "SchemaVersion", "SessionCount", "TotalDurationSeconds" }, document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\n', await File.ReadAllTextAsync(store.SettingsPath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain('\n', await File.ReadAllTextAsync(store.PresetsPath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain('\n', await File.ReadAllTextAsync(store.ModesPath, TestContext.Current.CancellationToken));
        var line = Assert.Single(await File.ReadAllLinesAsync(store.Journal.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(record.Id, JsonSerializer.Deserialize<SessionRecord>(line)!.Id);
        var restored = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(settings, restored.Settings);
        Assert.Equivalent(mode, Assert.Single(restored.Modes!));
        Assert.Equivalent(preset, Assert.Single(restored.Presets));
        Assert.Equal(record, Assert.Single(restored.Sessions));
        Assert.Equal(new SessionStatistics(1, 10), restored.Statistics);
    }

    [Fact]
    public async Task SettingsRejectDurationsAboveTheConfiguredTimerLimit()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(new(3, [], [], new(TimerLimitMinutes: 5),
            Modes: [new(Guid.NewGuid(), "Mode", DurationSeconds: 360)])));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(new(2, [], [], new(TimerLimitMinutes: 0))));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(new(2, [], [], new(TimerLimitMinutes: 121))));
    }

    [Fact]
    public async Task JournalRollsAtUtf8ByteLimitKeepsOnePreviousFileAndPreservesTotals()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var records = Enumerable.Range(0, 16).Select(i => Record(i, new string('字', 6000))).ToList();
        var data = new WorkspaceData(2, [], records, new());
        await store.SaveAsync(data);
        Assert.Equal(2, Directory.GetFiles(folder.Path, "*.jsonl").Length);
        Assert.InRange(new FileInfo(store.Journal.FilePath).Length, 1, SessionJournal.MaxFileBytes);
        Assert.InRange(new FileInfo(store.Journal.PreviousFilePath).Length, 1, SessionJournal.MaxFileBytes);
        var before = await File.ReadAllBytesAsync(store.Journal.FilePath, TestContext.Current.CancellationToken);
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
        var data = new WorkspaceData(2, [], records, new());
        await Task.WhenAll(store.SaveAsync(data), store.SaveAsync(data));
        Assert.Equal(8, (await File.ReadAllLinesAsync(store.Journal.FilePath, TestContext.Current.CancellationToken)).Length);
        Assert.Equal(new SessionStatistics(8, 80), (await store.LoadAsync()).Statistics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OldDevelopmentFormatIsResetWithoutMigration(int schema)
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { SchemaVersion = schema }), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(store.Journal.FilePath, "old data", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(store.ModesPath, "old modes", TestContext.Current.CancellationToken);
        var unrelated = Path.Combine(folder.Path, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep", TestContext.Current.CancellationToken);
        var data = await store.LoadAsync();
        Assert.Equal(4, data.SchemaVersion);
        Assert.Empty(data.Presets);
        Assert.Empty(data.Sessions);
        Assert.Empty(data.Modes!);
        Assert.Equal(new AppSettings(), data.Settings);
        Assert.Null(store.LoadWarning);
        Assert.False(File.Exists(store.Journal.FilePath));
        Assert.False(File.Exists(store.ModesPath));
        Assert.True(File.Exists(unrelated));
        await store.SaveAsync(data);
        Assert.Equal(0, (await store.LoadAsync()).Statistics!.SessionCount);
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
}
