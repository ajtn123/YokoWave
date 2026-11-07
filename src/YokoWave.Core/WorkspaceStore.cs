using YokoWave.Core.Localization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YokoWave.Core;

public sealed class WorkspaceStore(string directory)
{
    public string FilePath => Path.Combine(directory, "workspace.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private bool _loadFailed;
    public string? LoadWarning => _loadFailed ? L10n.Get("Error.CorruptWorkspace") : null;

    public async Task<WorkspaceData> LoadAsync()
    {
        if (!File.Exists(FilePath)) return WorkspaceData.Empty;
        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            var data = JsonSerializer.Deserialize<WorkspaceData>(json, JsonOptions)
                ?? throw new JsonException("Empty workspace file.");
            Validate(data);
            return data;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IOException or NotSupportedException)
        {
            // Preserve the original file for recovery. Never silently overwrite damaged data.
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
            if (_loadFailed) throw new LocalizedIOException("Error.CorruptWorkspace");
            Directory.CreateDirectory(directory);
            var temp = FilePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temp, FilePath, true);
        }
        finally { _gate.Release(); }
    }

    public static string ExportPresets(IEnumerable<WaveformPreset> presets) =>
        JsonSerializer.Serialize(presets.ToArray(), JsonOptions);

    public static IReadOnlyList<WaveformPreset> ImportPresets(string json)
    {
        if (json.Length > 1_000_000) throw new LocalizedArgumentException("Error.FileSize");
        var presets = JsonSerializer.Deserialize<List<WaveformPreset>>(json, JsonOptions)
            ?? throw new LocalizedArgumentException("Error.EmptyWaveFile");
        if (presets.Count is < 1 or > 100) throw new LocalizedArgumentException("Error.ImportCount");
        foreach (var preset in presets) { if (preset is null) throw new LocalizedArgumentException("Error.InvalidWave"); preset.Validate(); }
        if (presets.Select(p => p.Id).Distinct().Count() != presets.Count)
            throw new LocalizedArgumentException("Error.DuplicateIds");
        return presets;
    }

    private static void Validate(WorkspaceData data)
    {
        if (data.SchemaVersion != 1 || data.Presets is null || data.Sessions is null || data.Settings is null)
            throw new LocalizedArgumentException("Error.UnsupportedData");
        foreach (var preset in data.Presets) { if (preset is null) throw new LocalizedArgumentException("Error.InvalidWave"); preset.Validate(); }
        if (data.Presets.Select(p => p.Id).Distinct().Count() != data.Presets.Count || data.Sessions.Any(s => s is null))
            throw new LocalizedArgumentException("Error.InvalidRecords");
        if (!double.IsFinite(data.Settings.DefaultLimit) || data.Settings.DefaultLimit is < 0 or > 100 ||
            !double.IsFinite(data.Settings.SessionMinutes) || data.Settings.SessionMinutes is < 1 or > 120)
            throw new LocalizedArgumentException("Error.InvalidSettings");
    }
}
