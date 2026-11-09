using System.Text.Json;
using System.Text.Json.Serialization;

namespace YokoWave.Pipelines;

public sealed record NodeDefinition(Guid Id, string Type, JsonElement Configuration)
{
    public static NodeDefinition Create<T>(string type, T configuration, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), type, JsonSerializer.SerializeToElement(configuration, PipelineJson.Options));
}

public sealed record PortAddress(Guid Node, string Port);
public sealed record PipelineConnection(PortAddress Source, PortAddress Target);
public sealed record NodeLayout(Guid Node, double X, double Y);
public sealed record PipelineDocument(Guid Id, string Name, NodeDefinition[] Nodes, PipelineConnection[] Connections,
    NodeLayout[] Layout, double DurationSeconds = 90, bool TimerEnabled = false, int Version = 1,
    ExposedPort[]? Inputs = null, ExposedPort[]? Outputs = null);

public static class PipelineJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 256,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PipelineDocument Snapshot(PipelineDocument document) =>
        JsonSerializer.Deserialize<PipelineDocument>(JsonSerializer.Serialize(document, Options), Options)!;
}
