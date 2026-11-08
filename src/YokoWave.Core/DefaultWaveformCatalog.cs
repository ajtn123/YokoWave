using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public static class DefaultWaveformCatalog
{
    private sealed record Definition(WaveformPreset Preset, string NameKey);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly IReadOnlyDictionary<Guid, Definition> Definitions = Load();
    public static IReadOnlyList<WaveformPreset> Presets { get; } = new ReadOnlyCollection<WaveformPreset>(
        Definitions.Values.Select(d => d.Preset).ToList());

    public static bool Contains(Guid id) => Definitions.ContainsKey(id);
    public static string Name(Guid id) => Definitions.TryGetValue(id, out var definition)
        ? L10n.Get(definition.NameKey) : throw new KeyNotFoundException();

    private static IReadOnlyDictionary<Guid, Definition> Load()
    {
        List<Definition> entries = [];
        foreach (var name in new[] { "default-waveforms", "dglab-waveforms" })
        {
            using var stream = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream($"YokoWave.Core.Data.{name}.json")
                ?? throw new InvalidDataException("Default waveform catalog is missing.");
            using var document = JsonDocument.Parse(stream);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var preset = item.Deserialize<WaveformPreset>(Options) ?? throw new InvalidDataException("Invalid default waveform.");
                preset.Validate();
                var nameKey = item.GetProperty("NameKey").GetString();
                if (string.IsNullOrWhiteSpace(nameKey)) throw new InvalidDataException("Default waveform name key is missing.");
                entries.Add(new(preset, nameKey));
            }
        }
        if (entries.Count == 0 || entries.Select(d => d.Preset.Id).Distinct().Count() != entries.Count)
            throw new InvalidDataException("Default waveform identifiers must be unique.");
        return new ReadOnlyDictionary<Guid, Definition>(entries.ToDictionary(d => d.Preset.Id));
    }
}
