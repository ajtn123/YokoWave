using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed class WorkspaceStore(string directory)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loadFailed;
    private bool _loaded;

    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "workspace.json");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string WaveformsPath => Path.Combine(DirectoryPath, "waveforms.json");
    public SessionJournal Journal { get; } = new(Path.GetFullPath(directory));
    public SessionStatistics Statistics { get; private set; } = new();
    public string? LoadWarning => _loadFailed ? L10n.Get("Error.CorruptWorkspace") : null;

    public async Task<WorkspaceData> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(WorkspaceData data)
    {
        Validate(data);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_loaded)
                await LoadCoreAsync().ConfigureAwait(false);
            if (_loadFailed)
                throw new LocalizedIOException("Error.CorruptWorkspace");
            Directory.CreateDirectory(DirectoryPath);
            await WriteAtomicAsync(SettingsPath, data.Settings).ConfigureAwait(false);
            await WriteAtomicAsync(WaveformsPath, data.Waveforms).ConfigureAwait(false);
            await Journal.AppendAsync(data.Sessions, _jsonOptions, record => Statistics = Statistics.Add(record)).ConfigureAwait(false);
            if (data.Statistics is { } statistics)
            {
                Statistics = new(Math.Max(Statistics.SessionCount, statistics.SessionCount),
                    Math.Max(Statistics.TotalDurationSeconds, statistics.TotalDurationSeconds));
            }

            await WriteAtomicAsync(FilePath, new Metadata(WorkspaceData.CurrentSchema, Statistics.SessionCount, Statistics.TotalDurationSeconds)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (string? name in new[] { "workspace.json", "settings.json", "waveforms.json", "pipelines.json", "pipeline-presets.json", "sessions.jsonl", "sessions.1.jsonl", "modes.json", "presets.json" })
            {
                File.Delete(Path.Combine(DirectoryPath, name));
                File.Delete(Path.Combine(DirectoryPath, name + ".tmp"));
            }
            _loaded = false;
            _loadFailed = false;
            Statistics = new();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WorkspaceData> LoadCoreAsync()
    {
        _loaded = true;
        try
        {
            Metadata metadata = new();
            if (File.Exists(FilePath))
            {
                string json = await File.ReadAllTextAsync(FilePath).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version))
                    throw new LocalizedArgumentException("Error.UnsupportedData");
                if (version is >= 1 and < WorkspaceData.CurrentSchema)
                {
                    // Development format change: intentionally reset old data, without migration.
                    foreach (string? path in new[] { FilePath, SettingsPath, WaveformsPath, Path.Combine(DirectoryPath, "modes.json"), Path.Combine(DirectoryPath, "presets.json"),
                        Path.Combine(DirectoryPath, "pipelines.json"), Path.Combine(DirectoryPath, "pipeline-presets.json"), Journal.FilePath, Journal.PreviousFilePath })
                    {
                        File.Delete(path);
                    }

                    Statistics = new();
                    await Journal.LoadAsync(_jsonOptions).ConfigureAwait(false);
                    return WorkspaceData.Empty;
                }
                metadata = JsonSerializer.Deserialize<Metadata>(json, _jsonOptions) ?? throw new JsonException("Invalid workspace statistics.");
            }
            if (metadata.SchemaVersion != WorkspaceData.CurrentSchema || metadata.SessionCount < 0 || !double.IsFinite(metadata.TotalDurationSeconds) || metadata.TotalDurationSeconds < 0)
                throw new LocalizedArgumentException("Error.UnsupportedData");
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false), _jsonOptions) ?? throw new JsonException("Invalid settings.") : new();
            var waveforms = File.Exists(WaveformsPath)
                ? JsonSerializer.Deserialize<List<WaveformAsset>>(await File.ReadAllTextAsync(WaveformsPath).ConfigureAwait(false), _jsonOptions) ?? throw new JsonException("Invalid presets.") : [];
            await Journal.LoadAsync(_jsonOptions).ConfigureAwait(false);
            var sessions = Journal.Retained.ToList();
            Statistics = new(Math.Max(metadata.SessionCount, sessions.Count),
                Math.Max(metadata.TotalDurationSeconds, sessions.Sum(record => record.DurationSeconds)));
            var data = new WorkspaceData(WorkspaceData.CurrentSchema, waveforms, sessions, settings, Statistics);
            Validate(data);
            return data;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or NotSupportedException or InvalidOperationException)
        {
            _loadFailed = true;
            return WorkspaceData.Empty;
        }
    }

    private static void Validate(WorkspaceData data)
    {
        if (data.SchemaVersion != WorkspaceData.CurrentSchema || data.Waveforms is null || data.Sessions is null || data.Settings is null)
            throw new LocalizedArgumentException("Error.UnsupportedData");
        foreach (var waveform in data.Waveforms)
        {
            if (waveform is null)
                throw new LocalizedArgumentException("Error.InvalidWave");
            waveform.Validate();
        }
        if (data.Waveforms.Select(waveform => waveform.Id).Distinct().Count() != data.Waveforms.Count || data.Sessions.Any(record => record is null) ||
            data.Sessions.Select(record => record.Id).Distinct().Count() != data.Sessions.Count)
        {
            throw new LocalizedArgumentException("Error.InvalidRecords");
        }

        foreach (var record in data.Sessions)
            SessionJournal.ValidateRecord(record);
        if (data.Statistics is { } statistics && (statistics.SessionCount < 0 || !double.IsFinite(statistics.TotalDurationSeconds) || statistics.TotalDurationSeconds < 0))
            throw new LocalizedArgumentException("Error.InvalidRecords");
        var settings = data.Settings;
        if (settings.DeviceOutputs is { } outputs)
        {
            if (outputs.Any(o => o is null) || outputs.Select(o => o.DeviceId).Distinct(StringComparer.Ordinal).Count() != outputs.Count)
                throw new LocalizedArgumentException("Error.InvalidSettings");
            foreach (var output in outputs)
                output.Validate();
        }
        if (settings.AutoConnectDevices is { } devices && (devices.Count > 256 || devices.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 512) ||
            devices.Distinct(StringComparer.Ordinal).Count() != devices.Count))
        {
            throw new LocalizedArgumentException("Error.InvalidSettings");
        }

        if (settings.RemoteEndpoints is { } endpoints)
        {
            if (endpoints.Count > 16 || endpoints.Any(endpoint => endpoint is null) || endpoints.Select(endpoint => endpoint.Id).Distinct().Count() != endpoints.Count)
                throw new LocalizedArgumentException("Error.RemoteSettings");
            foreach (var endpoint in endpoints)
                endpoint.Validate();
        }
        if (!double.IsFinite(settings.DefaultLimit) || settings.DefaultLimit is < 0 or > 100 ||
            settings.TimerLimitMinutes is < 1 or > 120)
        {
            throw new LocalizedArgumentException("Error.InvalidSettings");
        }
    }

    private static async Task WriteAtomicAsync<T>(string path, T data)
    {
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(data, _jsonOptions)).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    private sealed record Metadata(int SchemaVersion = WorkspaceData.CurrentSchema, long SessionCount = 0, double TotalDurationSeconds = 0);
}
