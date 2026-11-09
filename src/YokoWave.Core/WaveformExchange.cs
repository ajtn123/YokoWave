using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

/// <summary>Imports and exports validated waveform assets independently of workspace persistence.</summary>
public static class WaveformExchange
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions _exportOptions = new(_jsonOptions) { WriteIndented = true };

    public static string Export(IEnumerable<WaveformAsset> waveforms) => JsonSerializer.Serialize(waveforms.ToArray(), _exportOptions);

    public static IReadOnlyList<WaveformAsset> Import(string json)
    {
        if (json.Length > 1_000_000)
            throw new LocalizedArgumentException("Error.FileSize");
        var waveforms = JsonSerializer.Deserialize<List<WaveformAsset>>(json, _jsonOptions) ?? throw new LocalizedArgumentException("Error.EmptyWaveFile");
        if (waveforms.Count is < 1 or > 100)
            throw new LocalizedArgumentException("Error.ImportCount");
        foreach (var waveform in waveforms)
        {
            if (waveform is null)
                throw new LocalizedArgumentException("Error.InvalidWave");
            waveform.Validate();
        }
        if (waveforms.Select(waveform => waveform.Id).Distinct().Count() != waveforms.Count)
            throw new LocalizedArgumentException("Error.DuplicateIds");
        return waveforms;
    }
}
