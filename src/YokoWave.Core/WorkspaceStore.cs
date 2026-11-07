using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed class WorkspaceStore(string directory)
{
    private sealed record Metadata(int SchemaVersion = 2, long SessionCount = 0, double TotalDurationSeconds = 0);
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "workspace.json");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string PresetsPath => Path.Combine(DirectoryPath, "presets.json");
    public SessionJournal Journal { get; } = new(Path.GetFullPath(directory));
    public SessionStatistics Statistics { get; private set; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions ExportOptions = new(JsonOptions) { WriteIndented = true };
    private bool _loadFailed;
    private bool _loaded;
    public string? LoadWarning => _loadFailed ? L10n.Get("Error.CorruptWorkspace") : null;

    public async Task<WorkspaceData> LoadAsync()
    {
        await _gate.WaitAsync();
        try { return await LoadCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task<WorkspaceData> LoadCoreAsync()
    {
        _loaded = true;
        try
        {
            Metadata metadata = new();
            if (File.Exists(FilePath))
            {
                var json = await File.ReadAllTextAsync(FilePath);
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
                    throw new LocalizedArgumentException("Error.UnsupportedData");
                if (version == 1)
                {
                    // Development format change: intentionally reset old data, without migration.
                    foreach (var path in new[] { FilePath, SettingsPath, PresetsPath, Journal.FilePath, Journal.PreviousFilePath }) File.Delete(path);
                    Statistics = new();
                    await Journal.LoadAsync(JsonOptions);
                    return WorkspaceData.Empty;
                }
                metadata = JsonSerializer.Deserialize<Metadata>(json, JsonOptions) ?? throw new JsonException("Invalid workspace statistics.");
            }
            if (metadata.SchemaVersion != 2 || metadata.SessionCount < 0 || !double.IsFinite(metadata.TotalDurationSeconds) || metadata.TotalDurationSeconds < 0)
                throw new LocalizedArgumentException("Error.UnsupportedData");
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(SettingsPath), JsonOptions) ?? throw new JsonException("Invalid settings.") : new();
            var presets = File.Exists(PresetsPath)
                ? JsonSerializer.Deserialize<List<WaveformPreset>>(await File.ReadAllTextAsync(PresetsPath), JsonOptions) ?? throw new JsonException("Invalid presets.") : [];
            await Journal.LoadAsync(JsonOptions);
            var sessions = Journal.Retained.ToList();
            Statistics = new(Math.Max(metadata.SessionCount, sessions.Count),
                Math.Max(metadata.TotalDurationSeconds, sessions.Sum(record => record.DurationSeconds)));
            var data = new WorkspaceData(2, presets, sessions, settings, Statistics);
            Validate(data);
            return data;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or NotSupportedException or InvalidOperationException)
        {
            _loadFailed = true;
            return WorkspaceData.Empty;
        }
    }

    public async Task SaveAsync(WorkspaceData data)
    {
        Validate(data);
        await _gate.WaitAsync();
        try
        {
            if (!_loaded) await LoadCoreAsync();
            if (_loadFailed) throw new LocalizedIOException("Error.CorruptWorkspace");
            Directory.CreateDirectory(DirectoryPath);
            await WriteAtomicAsync(SettingsPath, data.Settings);
            await WriteAtomicAsync(PresetsPath, data.Presets);
            await Journal.AppendAsync(data.Sessions, JsonOptions, record => Statistics = Statistics.Add(record));
            if (data.Statistics is { } statistics)
                Statistics = new(Math.Max(Statistics.SessionCount, statistics.SessionCount),
                    Math.Max(Statistics.TotalDurationSeconds, statistics.TotalDurationSeconds));
            await WriteAtomicAsync(FilePath, new Metadata(2, Statistics.SessionCount, Statistics.TotalDurationSeconds));
        }
        finally { _gate.Release(); }
    }

    private static async Task WriteAtomicAsync<T>(string path, T data)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporary, path, true);
    }

    public static string ExportPresets(IEnumerable<WaveformPreset> presets) => JsonSerializer.Serialize(presets.ToArray(), ExportOptions);

    public static IReadOnlyList<WaveformPreset> ImportPresets(string json)
    {
        if (json.Length > 1_000_000) throw new LocalizedArgumentException("Error.FileSize");
        var presets = JsonSerializer.Deserialize<List<WaveformPreset>>(json, JsonOptions) ?? throw new LocalizedArgumentException("Error.EmptyWaveFile");
        if (presets.Count is < 1 or > 100) throw new LocalizedArgumentException("Error.ImportCount");
        foreach (var preset in presets) { if (preset is null) throw new LocalizedArgumentException("Error.InvalidWave"); preset.Validate(); }
        if (presets.Select(preset => preset.Id).Distinct().Count() != presets.Count) throw new LocalizedArgumentException("Error.DuplicateIds");
        return presets;
    }

    private static void Validate(WorkspaceData data)
    {
        if (data.SchemaVersion is not (1 or 2) || data.Presets is null || data.Sessions is null || data.Settings is null)
            throw new LocalizedArgumentException("Error.UnsupportedData");
        foreach (var preset in data.Presets) { if (preset is null) throw new LocalizedArgumentException("Error.InvalidWave"); preset.Validate(); }
        if (data.Presets.Select(preset => preset.Id).Distinct().Count() != data.Presets.Count || data.Sessions.Any(record => record is null) ||
            data.Sessions.Select(record => record.Id).Distinct().Count() != data.Sessions.Count)
            throw new LocalizedArgumentException("Error.InvalidRecords");
        foreach (var record in data.Sessions) SessionJournal.ValidateRecord(record);
        if (data.Statistics is { } statistics && (statistics.SessionCount < 0 || !double.IsFinite(statistics.TotalDurationSeconds) || statistics.TotalDurationSeconds < 0))
            throw new LocalizedArgumentException("Error.InvalidRecords");
        var settings = data.Settings;
        if (!double.IsFinite(settings.DefaultLimit) || settings.DefaultLimit is < 0 or > 100 ||
            !double.IsFinite(settings.SessionMinutes) || settings.SessionMinutes is < 0 or > 120 || settings.SessionSeconds is < 0 or > 59 ||
            settings.SessionMinutes * 60 + settings.SessionSeconds > 7200 ||
            settings.TimerEnabled && settings.SessionMinutes * 60 + settings.SessionSeconds < 1)
            throw new LocalizedArgumentException("Error.InvalidSettings");
    }
}
