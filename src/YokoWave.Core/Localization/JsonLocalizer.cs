using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace YokoWave.Core.Localization;

public sealed class JsonLocalizer : INotifyPropertyChanged
{
    private readonly IReadOnlyDictionary<string, string> _english;
    private readonly IReadOnlyDictionary<string, string> _chinese;
    public static string SystemLanguage { get; } = NormalizeLanguage(CultureInfo.CurrentUICulture.Name);
    public string Language { get; private set; }
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyDictionary<string, string> English => _english;
    public IReadOnlyDictionary<string, string> Chinese => _chinese;

    public JsonLocalizer(string? language = null)
        : this(ReadCatalog("en"), ReadCatalog("zh-CN"), language ?? SystemLanguage) { }

    public JsonLocalizer(IReadOnlyDictionary<string, string> english,
        IReadOnlyDictionary<string, string> chinese, string language = "en")
    {
        _english = english;
        _chinese = chinese;
        Language = NormalizeLanguage(language);
    }

    public string this[string key] => Get(key);
    public string Get(string key)
    {
        var catalog = Language == "zh-CN" ? _chinese : _english;
        if (catalog.TryGetValue(key, out var value)) return value;
        return _english.TryGetValue(key, out value) ? value : $"[{key}]";
    }
    public string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments);

    public void SetLanguage(string? language)
    {
        var normalized = NormalizeLanguage(language);
        if (normalized == Language) return;
        Language = normalized;
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        PropertyChanged?.Invoke(this, new(nameof(Culture)));
        PropertyChanged?.Invoke(this, new("Item[]"));
    }

    // Also displays reason strings written by the original schema in either supported language.
    public string TranslateStored(string value, string prefix)
    {
        if (value.StartsWith(prefix, StringComparison.Ordinal) && _english.ContainsKey(value)) return Get(value);
        var match = _english.Concat(_chinese).FirstOrDefault(p => p.Key.StartsWith(prefix, StringComparison.Ordinal) && p.Value == value);
        return match.Key is null ? value : Get(match.Key);
    }

    public static string NormalizeLanguage(string? language) =>
        language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? "zh-CN" : "en";

    private static IReadOnlyDictionary<string, string> ReadCatalog(string language)
    {
        using var stream = typeof(JsonLocalizer).Assembly.GetManifestResourceStream($"YokoWave.Core.Locales.{language}.json")
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
