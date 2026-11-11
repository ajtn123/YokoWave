using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace YokoWave.Core.Localization;

public sealed class JsonLocalizer : INotifyPropertyChanged
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _catalogs;
    public static IReadOnlyList<string> SupportedLanguages { get; } = Array.AsReadOnly(new[] { "en", "zh-CN", "ja", "ko" });
    public static string SystemLanguage { get; } = NormalizeLanguage(CultureInfo.CurrentUICulture.Name);
    public string Language { get; private set; }
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public IReadOnlyDictionary<string, string> English => _catalogs["en"];

    public string this[string key] => Get(key);
    public event PropertyChangedEventHandler? PropertyChanged;

    public JsonLocalizer(string? language = null)
        : this(SupportedLanguages.ToDictionary(code => code, ReadCatalog, StringComparer.Ordinal), language ?? SystemLanguage) { }

    public JsonLocalizer(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> catalogs, string language = "en")
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        if (!catalogs.ContainsKey("en"))
            throw new ArgumentException("An English fallback catalog is required.", nameof(catalogs));
        _catalogs = catalogs;
        Language = NormalizeLanguage(language);
    }
    public IReadOnlyDictionary<string, string> GetCatalog(string language) => _catalogs.GetValueOrDefault(NormalizeLanguage(language), English);

    public string Get(string key)
    {
        var catalog = GetCatalog(Language);
        if (catalog.TryGetValue(key, out string? value))
            return value;
        return English.TryGetValue(key, out value) ? value : $"[{key}]";
    }
    public string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments);

    public void SetLanguage(string? language)
    {
        string normalized = NormalizeLanguage(language);
        if (normalized == Language)
            return;
        Language = normalized;
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        PropertyChanged?.Invoke(this, new(nameof(Culture)));
        PropertyChanged?.Invoke(this, new("Item[]"));
    }

    // Stored display strings can come from any supported language.
    public string TranslateStored(string value, string prefix)
    {
        if (value.StartsWith(prefix, StringComparison.Ordinal) && English.ContainsKey(value))
            return Get(value);
        var match = _catalogs.Values.SelectMany(catalog => catalog).FirstOrDefault(p => p.Key.StartsWith(prefix, StringComparison.Ordinal) && p.Value == value);
        return match.Key is null ? value : Get(match.Key);
    }

    public static string NormalizeLanguage(string? language)
    {
        string code = language?.Split('-', '_')[0].ToLowerInvariant() ?? "en";
        return code switch { "zh" => "zh-CN", "ja" => "ja", "ko" => "ko", _ => "en" };
    }

    private static IReadOnlyDictionary<string, string> ReadCatalog(string language)
    {
        using var stream = typeof(JsonLocalizer).Assembly.GetManifestResourceStream($"YokoWave.Locales.App.{language}.json")
            ?? throw new InvalidOperationException($"Missing localization catalog: {language}");
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String || !result.TryAdd(entry.Name, entry.Value.GetString()!))
                throw new InvalidDataException($"Invalid or duplicate localization key: {entry.Name}");
        }
        return result;
    }
}

public static class L10n
{
    public static JsonLocalizer Current { get; } = new();
    public static string Get(string key) => Current.Get(key);
    public static string Format(string key, params object?[] arguments) => Current.Format(key, arguments);
}

public sealed record LocalizedMessage(string Key, object?[] Arguments, bool IsLiteral = false)
{
    public string Value => IsLiteral ? Key : L10n.Format(Key, Arguments);
    public override string ToString() => Value;
}
