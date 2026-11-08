using System.Text.Json;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

internal static class SettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException();
        }
        catch (JsonException error)
        {
            throw new LocalizedArgumentException("Error.ProtocolJson", error);
        }
    }
}
