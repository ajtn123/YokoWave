using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public static class DefaultWaveformCatalog
{
    private sealed record Definition(Guid Id, string Name, string NameKey, WaveformFamily Family,
        int SourceId, string? DeviceCode, double SampleIntervalSeconds, WaveformSample[] Frames);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly IReadOnlyDictionary<Guid, Definition> Definitions = Load();
    public static IReadOnlyList<WaveformPreset> Presets { get; } = new ReadOnlyCollection<WaveformPreset>(
        Definitions.Values.Select(d => new WaveformPreset(d.Id, d.Name, WaveShape.Sampled,
            d.Frames.Length * d.SampleIntervalSeconds, 1, Samples: d.Frames, Family: d.Family)).ToList());

    public static bool Contains(Guid id) => Definitions.ContainsKey(id);
    public static string Name(Guid id) => Definitions.TryGetValue(id, out var definition)
        ? L10n.Get(definition.NameKey) : throw new KeyNotFoundException();

    private static IReadOnlyDictionary<Guid, Definition> Load()
    {
        using var stream = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream("YokoWave.Core.Data.default-waveforms.json")
            ?? throw new InvalidDataException("Default waveform catalog is missing.");
        var entries = JsonSerializer.Deserialize<Definition[]>(stream, Options)
            ?? throw new InvalidDataException("Default waveform catalog is empty.");
        if (entries.Length == 0 || entries.Select(d => d.Id).Distinct().Count() != entries.Length)
            throw new InvalidDataException("Default waveform identifiers must be unique.");
        foreach (var entry in entries)
        {
            if (!double.IsFinite(entry.SampleIntervalSeconds) || entry.SampleIntervalSeconds <= 0 || entry.Frames is null)
                throw new InvalidDataException("Invalid default waveform timing.");
            new WaveformPreset(entry.Id, entry.Name, WaveShape.Sampled,
                entry.Frames.Length * entry.SampleIntervalSeconds, 1, Samples: entry.Frames, Family: entry.Family).Validate();
        }
        return new ReadOnlyDictionary<Guid, Definition>(entries.ToDictionary(d => d.Id));
    }
}
