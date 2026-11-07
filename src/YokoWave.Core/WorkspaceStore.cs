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
    public string? LoadWarning { get; private set; }

    public async Task<WorkspaceData> LoadAsync()
    {
        if (!File.Exists(FilePath)) return WorkspaceData.Empty;
        try
        {
            var json = await File.ReadAllTextAsync(FilePath);
            var data = JsonSerializer.Deserialize<WorkspaceData>(json, JsonOptions)
                ?? throw new JsonException("空文件");
            Validate(data);
            return data;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IOException or NotSupportedException)
        {
            // Preserve the original file for recovery. Never silently overwrite damaged data.
            LoadWarning = "本地数据无法读取，已保留原文件。请检查或备份后移走 workspace.json。";
            return WorkspaceData.Empty;
        }
    }

    public async Task SaveAsync(WorkspaceData data)
    {
        Validate(data);
        await _gate.WaitAsync();
        try
        {
            if (LoadWarning is not null) throw new IOException(LoadWarning);
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
        if (json.Length > 1_000_000) throw new ArgumentException("波形文件不能超过 1 MB。");
        var presets = JsonSerializer.Deserialize<List<WaveformPreset>>(json, JsonOptions)
            ?? throw new ArgumentException("波形文件为空。");
        if (presets.Count is < 1 or > 100) throw new ArgumentException("每次可导入 1–100 个波形。");
        foreach (var preset in presets) { if (preset is null) throw new ArgumentException("无效波形。"); preset.Validate(); }
        if (presets.Select(p => p.Id).Distinct().Count() != presets.Count)
            throw new ArgumentException("文件中存在重复的波形标识。");
        return presets;
    }

    private static void Validate(WorkspaceData data)
    {
        if (data.SchemaVersion != 1 || data.Presets is null || data.Sessions is null || data.Settings is null)
            throw new ArgumentException("不支持的数据格式。");
        foreach (var preset in data.Presets) { if (preset is null) throw new ArgumentException("无效波形。"); preset.Validate(); }
        if (data.Presets.Select(p => p.Id).Distinct().Count() != data.Presets.Count || data.Sessions.Any(s => s is null))
            throw new ArgumentException("本地数据包含无效记录。");
        if (!double.IsFinite(data.Settings.DefaultLimit) || data.Settings.DefaultLimit is < 0 or > 100 ||
            !double.IsFinite(data.Settings.SessionMinutes) || data.Settings.SessionMinutes is < 1 or > 120)
            throw new ArgumentException("无效设置。");
    }
}
