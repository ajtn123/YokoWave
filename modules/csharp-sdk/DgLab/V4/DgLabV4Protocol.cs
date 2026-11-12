using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YokoWave.Core.Localization;

namespace YokoWave.Sdk.DgLab.V4;

public sealed class DgLabV4Exception(string code) : Exception, ILocalizedError
{
    public string Code { get; } = code;
    public LocalizedMessage LocalizedMessage { get; } = code is "invalid_params" or "invalid_operate" or "slot_not_found" or "method_not_found" or
        "duplicate_request_id" or "internal_error" or "client_not_found" or "controller_not_found" or "bad_request" or "queue_full"
        ? new("DgLab.Error." + code, []) : new("DgLab.Error", [code]);
    public override string Message => LocalizedMessage.Value;
}

public sealed record DgLabV4Device(string SlotId, string Name, string Type, JsonObject? Props = null, JsonObject? SlotState = null);
public sealed record DgLabV4Operation(
    [property: JsonPropertyName("s")] string SlotId,
    [property: JsonPropertyName("t")] int Type,
    [property: JsonPropertyName("c")] int Channel,
    [property: JsonPropertyName("v")] JsonElement Value,
    [property: JsonPropertyName("p")] int Priority = 1,
    [property: JsonPropertyName("d")] double Duration = 0,
    [property: JsonPropertyName("im")] bool Immediate = false,
    [property: JsonPropertyName("ver")] int Version = 3,
    [property: JsonPropertyName("seq")] int? Sequence = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SlotId) || SlotId.Length > 256 || Channel is < 0 or > 1 || Priority is < 0 or > 2 ||
            !double.IsFinite(Duration) || Duration is < 0 or > 7_200_000 || Type is not (0 or 3 or 4 or 7) ||
            Version is not (2 or 3) || Sequence is < 0 or > 255)
        {
            throw new DgLabV4Exception("invalid_operate");
        }
        if (Type == 0)
        {
            _ = DgLabV4Pulse.Decode(Value, Version);
        }
        else if (Value.ValueKind != JsonValueKind.Number || !Value.TryGetDouble(out double level) || !double.IsFinite(level) || level is < -1_000_000 or > 1_000_000 ||
            Type == 4 && level < 0 || Type == 7 && level != 0)
        {
            throw new DgLabV4Exception("invalid_operate");
        }
    }
}

public static class DgLabV4Protocol
{
    internal static string? TargetController(string query)
    {
        var parameters = System.Web.HttpUtility.ParseQueryString(query);
        return parameters.GetValues("targetId")?.FirstOrDefault() ?? parameters.GetValues("tid")?.FirstOrDefault();
    }

    public static string String(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
        ? field.GetString()! : throw new DgLabV4Exception("invalid_params");
    public static JsonElement Data(JsonElement frame) => frame.TryGetProperty("data", out var data)
        ? data : RemoteProtocol.Element(new { });
    public static T Read<T>(JsonElement value)
    {
        try
        {
            return RemoteProtocol.Read<T>(value);
        }
        catch (Exception error) when (error is JsonException or RemoteRpcException)
        {
            throw new DgLabV4Exception("invalid_params");
        }
    }
    public static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var pair in patch)
        {
            if (pair.Value is JsonObject nested && target[pair.Key] is JsonObject existing)
                Merge(existing, nested);
            else
                target[pair.Key] = pair.Value?.DeepClone();
        }
    }
    public static Uri ControllerUri(string url)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
        {
            throw new LocalizedArgumentException("Error.RemoteSettings");
        }
        return uri;
    }
    public static Uri AppUri(string url, string controllerId)
    {
        var uri = ControllerUri(url);
        if (string.IsNullOrWhiteSpace(controllerId) || controllerId.Length > 256)
            throw new LocalizedArgumentException("Error.RemoteSettings");
        return new UriBuilder(uri) { Query = "tid=" + Uri.EscapeDataString(controllerId) }.Uri;
    }
    public static string PairingUrl(string url, string controllerId) =>
        "https://dungeon-lab.cn/s/?v=1&action=socket&url=" + Uri.EscapeDataString(AppUri(url, controllerId).ToString());
}
