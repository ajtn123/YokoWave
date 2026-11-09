using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

internal static class SettingsJson
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, _options);
    public static T Read<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, _options) ?? throw new JsonException();
        }
        catch (JsonException error)
        {
            throw new LocalizedArgumentException("Error.ProtocolJson", error);
        }
    }
}
