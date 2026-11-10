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
    private static readonly IReadOnlyDictionary<Guid, WaveformAsset> _definitions = Load();
    public static IReadOnlyList<WaveformAsset> Waveforms { get; } = new ReadOnlyCollection<WaveformAsset>(
        _definitions.Values.ToList());

    public static bool Contains(Guid id) => _definitions.ContainsKey(id);
    public static string Name(Guid id) => _definitions.TryGetValue(id, out var definition)
        ? definition.GetName(L10n.Current.Culture) : throw new KeyNotFoundException();

    private static IReadOnlyDictionary<Guid, WaveformAsset> Load()
    {
        List<WaveformAsset> entries = [];
        foreach (string? name in new[] { "yokonex-waveforms", "coyote-waveforms", "ovc-waveforms" })
        {
            using var stream = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream($"YokoWave.Core.Data.{name}.json")
                ?? throw new InvalidDataException("Default waveform catalog is missing.");
            using var document = JsonDocument.Parse(stream);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var waveform = item.Deserialize<WaveformAsset>(_options) ?? throw new InvalidDataException("Invalid default waveform.");
                waveform.Validate();
                if (waveform.Names?.ContainsKey("en") != true || !waveform.Names.ContainsKey("zh-CN") || waveform.Source is null)
                    throw new InvalidDataException("Default waveform names or source are missing.");
                entries.Add(waveform);
            }
        }
        if (entries.Count == 0 || entries.Select(d => d.Id).Distinct().Count() != entries.Count)
            throw new InvalidDataException("Default waveform identifiers must be unique.");
        return new ReadOnlyDictionary<Guid, WaveformAsset>(entries.ToDictionary(d => d.Id));
    }
}
