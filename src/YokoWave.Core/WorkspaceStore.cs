using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed class WorkspaceStore(string directory)
{
    private sealed record Metadata(int SchemaVersion = WorkspaceData.CurrentSchema, long SessionCount = 0, double TotalDurationSeconds = 0);
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "workspace.json");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string WaveformsPath => Path.Combine(DirectoryPath, "waveforms.json");
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
        try
        {
            return await LoadCoreAsync();
        }
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
                if (version is >= 1 and < WorkspaceData.CurrentSchema)
                {
                    // Development format change: intentionally reset old data, without migration.
                    foreach (var path in new[] { FilePath, SettingsPath, WaveformsPath, Path.Combine(DirectoryPath, "modes.json"), Path.Combine(DirectoryPath, "presets.json"),
                        Path.Combine(DirectoryPath, "pipelines.json"), Path.Combine(DirectoryPath, "pipeline-presets.json"), Journal.FilePath, Journal.PreviousFilePath })
                        File.Delete(path);
                    Statistics = new();
                    await Journal.LoadAsync(JsonOptions);
                    return WorkspaceData.Empty;
                }
                metadata = JsonSerializer.Deserialize<Metadata>(json, JsonOptions) ?? throw new JsonException("Invalid workspace statistics.");
            }
            if (metadata.SchemaVersion != WorkspaceData.CurrentSchema || metadata.SessionCount < 0 || !double.IsFinite(metadata.TotalDurationSeconds) || metadata.TotalDurationSeconds < 0)
                throw new LocalizedArgumentException("Error.UnsupportedData");
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(SettingsPath), JsonOptions) ?? throw new JsonException("Invalid settings.") : new();
            var presets = File.Exists(WaveformsPath)
                ? JsonSerializer.Deserialize<List<WaveformPreset>>(await File.ReadAllTextAsync(WaveformsPath), JsonOptions) ?? throw new JsonException("Invalid presets.") : [];
            await Journal.LoadAsync(JsonOptions);
            var sessions = Journal.Retained.ToList();
            Statistics = new(Math.Max(metadata.SessionCount, sessions.Count),
                Math.Max(metadata.TotalDurationSeconds, sessions.Sum(record => record.DurationSeconds)));
            var data = new WorkspaceData(WorkspaceData.CurrentSchema, presets, sessions, settings, Statistics);
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
            if (!_loaded)
                await LoadCoreAsync();
            if (_loadFailed)
                throw new LocalizedIOException("Error.CorruptWorkspace");
            Directory.CreateDirectory(DirectoryPath);
            await WriteAtomicAsync(SettingsPath, data.Settings);
            await WriteAtomicAsync(WaveformsPath, data.Presets);
            await Journal.AppendAsync(data.Sessions, JsonOptions, record => Statistics = Statistics.Add(record));
            if (data.Statistics is { } statistics)
                Statistics = new(Math.Max(Statistics.SessionCount, statistics.SessionCount),
                    Math.Max(Statistics.TotalDurationSeconds, statistics.TotalDurationSeconds));
            await WriteAtomicAsync(FilePath, new Metadata(WorkspaceData.CurrentSchema, Statistics.SessionCount, Statistics.TotalDurationSeconds));
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
        if (json.Length > 1_000_000)
            throw new LocalizedArgumentException("Error.FileSize");
        var presets = JsonSerializer.Deserialize<List<WaveformPreset>>(json, JsonOptions) ?? throw new LocalizedArgumentException("Error.EmptyWaveFile");
        if (presets.Count is < 1 or > 100)
            throw new LocalizedArgumentException("Error.ImportCount");
        foreach (var preset in presets)
        {
            if (preset is null)
                throw new LocalizedArgumentException("Error.InvalidWave");
            preset.Validate();
        }
        if (presets.Select(preset => preset.Id).Distinct().Count() != presets.Count)
            throw new LocalizedArgumentException("Error.DuplicateIds");
        return presets;
    }

    private static void Validate(WorkspaceData data)
    {
        if (data.SchemaVersion != WorkspaceData.CurrentSchema || data.Presets is null || data.Sessions is null || data.Settings is null)
            throw new LocalizedArgumentException("Error.UnsupportedData");
        foreach (var preset in data.Presets)
        {
            if (preset is null)
                throw new LocalizedArgumentException("Error.InvalidWave");
            preset.Validate();
        }
        if (data.Presets.Select(preset => preset.Id).Distinct().Count() != data.Presets.Count || data.Sessions.Any(record => record is null) ||
            data.Sessions.Select(record => record.Id).Distinct().Count() != data.Sessions.Count)
            throw new LocalizedArgumentException("Error.InvalidRecords");
        foreach (var record in data.Sessions)
            SessionJournal.ValidateRecord(record);
        if (data.Statistics is { } statistics && (statistics.SessionCount < 0 || !double.IsFinite(statistics.TotalDurationSeconds) || statistics.TotalDurationSeconds < 0))
            throw new LocalizedArgumentException("Error.InvalidRecords");
        var settings = data.Settings;
        if (settings.RemoteEndpoints is { } endpoints)
        {
            if (endpoints.Count > 16 || endpoints.Any(endpoint => endpoint is null) || endpoints.Select(endpoint => endpoint.Id).Distinct().Count() != endpoints.Count)
                throw new LocalizedArgumentException("Error.RemoteSettings");
            foreach (var endpoint in endpoints)
                endpoint.Validate();
        }
        if (!double.IsFinite(settings.DefaultLimit) || settings.DefaultLimit is < 0 or > 100 ||
            settings.TimerLimitMinutes is < 1 or > 120)
            throw new LocalizedArgumentException("Error.InvalidSettings");
    }
}
