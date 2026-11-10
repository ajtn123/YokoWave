using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public static class DefaultWaveformCatalog
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly IReadOnlyDictionary<Guid, Definition> _definitions = Load();
    public static IReadOnlyList<WaveformAsset> Waveforms { get; } = new ReadOnlyCollection<WaveformAsset>(
        _definitions.Values.Select(d => d.Asset).ToList());

    public static bool Contains(Guid id) => _definitions.ContainsKey(id);
    public static string Name(Guid id) => _definitions.TryGetValue(id, out var definition)
        ? L10n.Get(definition.NameKey) : throw new KeyNotFoundException();

    private static IReadOnlyDictionary<Guid, Definition> Load()
    {
        List<Definition> entries = [];
        foreach (string? name in new[] { "yokonex-waveforms", "coyote-waveforms", "ovc-waveforms" })
        {
            using var stream = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream($"YokoWave.Core.Data.{name}.json")
                ?? throw new InvalidDataException("Default waveform catalog is missing.");
            using var document = JsonDocument.Parse(stream);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var waveform = item.Deserialize<WaveformAsset>(_options) ?? throw new InvalidDataException("Invalid default waveform.");
                waveform.Validate();
                string? nameKey = item.GetProperty("NameKey").GetString();
                if (string.IsNullOrWhiteSpace(nameKey))
                    throw new InvalidDataException("Default waveform name key is missing.");
                entries.Add(new(waveform, nameKey));
            }
        }
        if (entries.Count == 0 || entries.Select(d => d.Asset.Id).Distinct().Count() != entries.Count)
            throw new InvalidDataException("Default waveform identifiers must be unique.");
        return new ReadOnlyDictionary<Guid, Definition>(entries.ToDictionary(d => d.Asset.Id));
    }

    private sealed record Definition(WaveformAsset Asset, string NameKey);
}
