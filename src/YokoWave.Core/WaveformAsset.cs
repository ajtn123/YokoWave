using System.Globalization;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed record WaveformAsset(
    [property: JsonRequired] Guid Id, [property: JsonRequired] string Name, [property: JsonRequired] WaveShape Shape, [property: JsonRequired] double PeriodSeconds,
    [property: JsonRequired] double Frequency, double[]? Points = null, WaveformSample[]? Samples = null,
    [property: JsonRequired] int FormatVersion = 1)
{
    public const int MaximumSamples = 512;
    public static IReadOnlyList<WaveformAsset> BuiltIn => DefaultWaveformCatalog.Waveforms;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyDictionary<string, string>? Names { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public WaveformSource? Source { get; init; }
    [JsonIgnore]
    public int ChannelCount => Shape == WaveShape.Sampled && Samples is { Length: > 0 }
        ? Samples.Max(sample => sample.Channels?.Length ?? 1) : 1;

    public void Validate()
    {
        if (!TryValidate(out string? errorKey))
            throw new LocalizedArgumentException(errorKey!);
    }

    public string GetName(CultureInfo culture)
    {
        for (var current = culture; current.Name.Length > 0; current = current.Parent)
        {
            if (Names?.TryGetValue(current.Name, out string? value) == true && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return Name;
    }

    public bool TryValidate(out string? errorKey)
    {
        errorKey = GetValidationError();
        return errorKey is null;
    }

    private string? GetValidationError()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
            return "Error.WaveName";
        if (Names is not null && (Names.Count is < 1 or > 32 || Names.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 35 ||
            string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 60)))
            return "Error.WaveName";
        if (FormatVersion != 1)
            return "Error.UnsupportedWaveFormat";
        if (Source is { IsValid: false })
            return "Error.WaveSource";
        if (!Enum.IsDefined(Shape) || !double.IsFinite(PeriodSeconds) || PeriodSeconds is < 0.2 or > 60)
            return "Error.Period";
        if (!double.IsFinite(Frequency) || Frequency is < 1 or > 100)
            return "Error.Frequency";
        if (Shape == WaveShape.Custom && (Points is null || Points.Length is < 2 or > 256 ||
            Points.Any(x => !double.IsFinite(x) || x is < 0 or > 1)))
        {
            return "Error.Points";
        }

        if (Shape == WaveShape.Sampled && (Samples is null || Samples.Length is < 2 or > MaximumSamples || Samples.Any(sample =>
            sample is null || !sample.IsValid)))
        {
            return "Error.Samples";
        }

        return null;
    }
}
