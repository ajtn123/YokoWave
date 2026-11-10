using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core;

namespace YokoWave.Tests.Storage;

public sealed class WorkspaceStoreTests
{
    public static TheoryData<int> OlderSchemas => new() { 1, WorkspaceData.CurrentSchema - 1 };
    public static TheoryData<int> UnsupportedSchemas => new() { 0, WorkspaceData.CurrentSchema + 1 };

    [Fact]
    public async Task WorkspaceRoundTripsAndPreservesCorruptOriginal()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var custom = WaveformAsset.BuiltIn.First(wave => wave.Source is not null) with
        {
            Id = Guid.NewGuid(),
            Name = "自定义"
        };
        await store.SaveAsync(new(WorkspaceData.CurrentSchema, [custom], [], new(35)));
        var loaded = await store.LoadAsync();
        Assert.Equivalent(custom, Assert.Single(loaded.Waveforms));
        Assert.Equal(35, loaded.Settings.DefaultLimit);
        await File.WriteAllTextAsync(store.FilePath, "broken", TestContext.Current.CancellationToken);
        Assert.Empty((await store.LoadAsync()).Waveforms);
        Assert.NotNull(store.LoadWarning);
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(WorkspaceData.Empty));
        Assert.Equal("broken", await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkspaceContainsOnlyStatisticsAndStorageUsesCompactSeparateFiles()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var waveform = DefaultWaveformCatalog.Waveforms[0] with
        {
            Id = Guid.NewGuid(),
            Name = "custom"
        };
        var record = Record(1);
        var settings = new AppSettings(TimerLimitMinutes: 5);
        await store.SaveAsync(new(WorkspaceData.CurrentSchema, [waveform], [record], settings));
        string json = await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "SchemaVersion", "SessionCount", "TotalDurationSeconds" }, document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\n', await File.ReadAllTextAsync(store.SettingsPath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain('\n', await File.ReadAllTextAsync(store.WaveformsPath, TestContext.Current.CancellationToken));
        Assert.Equal("waveforms.json", Path.GetFileName(store.WaveformsPath));
        Assert.False(File.Exists(Path.Combine(folder.Path, "presets.json")));
        string line = Assert.Single(await File.ReadAllLinesAsync(store.Journal.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal(record.Id, JsonSerializer.Deserialize<SessionRecord>(line, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!.Id);
        var restored = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(settings, restored.Settings);
        Assert.Equivalent(waveform, Assert.Single(restored.Waveforms));
        Assert.Equal(record, Assert.Single(restored.Sessions));
        Assert.Equal(new SessionStatistics(1, 10), restored.Statistics);
    }

    [Fact]
    public async Task SettingsRejectDurationsAboveTheConfiguredTimerLimit()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(new(WorkspaceData.CurrentSchema, [], [], new(TimerLimitMinutes: 0))));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(new(WorkspaceData.CurrentSchema, [], [], new(TimerLimitMinutes: 121))));
    }

    [Theory]
    [MemberData(nameof(OlderSchemas))]
    public async Task OlderDevelopmentSchemasResetKnownDataAndPreserveUnrelatedFiles(int schema)
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { SchemaVersion = schema }), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(store.Journal.FilePath, "old data", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "modes.json"), "old modes", TestContext.Current.CancellationToken);
        string unrelated = Path.Combine(folder.Path, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep", TestContext.Current.CancellationToken);
        var data = await store.LoadAsync();
        Assert.Equal(WorkspaceData.CurrentSchema, data.SchemaVersion);
        Assert.Empty(data.Waveforms);
        Assert.Empty(data.Sessions);
        Assert.Equal(new AppSettings(), data.Settings);
        Assert.Null(store.LoadWarning);
        Assert.False(File.Exists(store.Journal.FilePath));
        Assert.False(File.Exists(Path.Combine(folder.Path, "modes.json")));
        Assert.True(File.Exists(unrelated));
        await store.SaveAsync(data);
        Assert.Equal(0, (await store.LoadAsync()).Statistics!.SessionCount);
    }

    [Theory]
    [MemberData(nameof(UnsupportedSchemas))]
    public async Task UnsupportedSchemasPreserveFilesAndPreventSaving(int schema)
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        string metadata = JsonSerializer.Serialize(new { SchemaVersion = schema });
        await File.WriteAllTextAsync(store.FilePath, metadata, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(store.Journal.FilePath, "preserve this journal", TestContext.Current.CancellationToken);

        var data = await store.LoadAsync();

        Assert.Equal(WorkspaceData.CurrentSchema, data.SchemaVersion);
        Assert.Empty(data.Waveforms);
        Assert.Empty(data.Sessions);
        Assert.Equal(new AppSettings(), data.Settings);
        Assert.Equal(new SessionStatistics(), data.Statistics);
        Assert.NotNull(store.LoadWarning);
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(data));
        Assert.Equal(metadata, await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
        Assert.Equal("preserve this journal", await File.ReadAllTextAsync(store.Journal.FilePath, TestContext.Current.CancellationToken));
    }

    private static SessionRecord Record(int index, string? reason = null) => new(Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddSeconds(index), 10, "波形", 1, 20, 10, reason ?? "Reason.Manual");
}
