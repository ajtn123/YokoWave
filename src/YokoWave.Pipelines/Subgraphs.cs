using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YokoWave.Pipelines;

public sealed record ExposedPort(string Id, string NameKey, PortAddress Address, NumericParameterSpec? Parameter = null, PortValueKind Kind = PortValueKind.Numeric);
public sealed record SubgraphNode(PipelineDocument Graph, ExposedPort[] Inputs, ExposedPort[] Outputs,
    Dictionary<string, string>? Devices = null);
public sealed record PipelinePreset(Guid Id, string Name, PipelineDocument Graph, ExposedPort[] Inputs, ExposedPort[] Outputs);

/// <summary>Expands copied graph definitions into one DAG and one resource reservation.</summary>
public static class SubgraphExpansion
{
    public const string Type = "graph.subgraph";
    private sealed class NoopProcessor : ISignalProcessor
    {
        public void Evaluate(NodeEvaluationContext context)
        {
        }
    }

    public static NodeRegistration Registration() => new NodeRegistration<SubgraphNode>(Type, "Pipeline.Node.Subgraph", [],
        c => c.Graph is not null && c.Inputs is not null && c.Outputs is not null &&
            c.Inputs.Select(p => p.Id).Distinct().Count() == c.Inputs.Length && c.Outputs.Select(p => p.Id).Distinct().Count() == c.Outputs.Length,
        (_, _) => new NoopProcessor(), dynamicPorts: c =>
            c.Inputs.Select(p => new NodePort(p.Id, p.NameKey, PortDirection.Input, p.Parameter ?? new("", double.MinValue, double.MaxValue, 0), p.Kind))
                .Concat(c.Outputs.Select(p => new NodePort(p.Id, p.NameKey, PortDirection.Output, Kind: p.Kind))).ToArray());

    public static NodeDefinition MapDevices(NodeDefinition node, IReadOnlyDictionary<string, string> devices)
    {
        var json = JsonNode.Parse(node.Configuration.GetRawText())!;
        void Visit(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                foreach (var (key, item) in obj.ToArray())
                {
                    if (key == "DeviceId" && item is JsonValue scalar && scalar.TryGetValue<string>(out var id) && devices.TryGetValue(id, out var mapped))
                        obj[key] = mapped;
                    else if (key == "DeviceIds" && item is JsonArray array)
                    {
                        for (var i = 0; i < array.Count; i++)
                            if (array[i] is JsonValue element && element.TryGetValue<string>(out var original) && devices.TryGetValue(original, out var replacement))
                                array[i] = replacement;
                    }
                    else if (key == "Devices" && item is JsonObject map)
                    {
                        foreach (var entry in map.ToArray())
                            if (entry.Value is JsonValue original && original.TryGetValue<string>(out var target) && devices.TryGetValue(target, out var replacement))
                                map[entry.Key] = replacement;
                    }
                    else
                        Visit(item);
                }
            }
            else if (value is JsonArray array)
                foreach (var item in array)
                    Visit(item);
        }
        Visit(json);
        return node with
        {
            Configuration = JsonSerializer.SerializeToElement(json, PipelineJson.Options)
        };
    }

    public static PipelineDocument Expand(PipelineDocument document) => ExpandCore(document, 0).Document;
    private sealed record Expansion(PipelineDocument Document, Dictionary<PortAddress, PortAddress> Inputs, Dictionary<PortAddress, PortAddress> Outputs);
    private static Expansion ExpandCore(PipelineDocument document, int depth)
    {
        if (depth > 16)
            throw new PipelineValidationException([new("Pipeline.Error.SubgraphDepth")]);
        List<NodeDefinition> nodes = [];
        List<PipelineConnection> connections = [];
        List<NodeLayout> layout = [];
        Dictionary<PortAddress, PortAddress> inputs = [], outputs = [];
        foreach (var node in document.Nodes)
        {
            if (node.Type != Type)
            {
                nodes.Add(node);
                layout.AddRange(document.Layout.Where(l => l.Node == node.Id));
                continue;
            }
            var subgraph = node.Configuration.Deserialize<SubgraphNode>(PipelineJson.Options) ?? throw new JsonException();
            if (subgraph.Graph?.Nodes is null || subgraph.Graph.Connections is null || subgraph.Graph.Layout is null || subgraph.Inputs is null || subgraph.Outputs is null)
                throw new PipelineValidationException([new("Pipeline.Error.Configuration", node.Id)]);
            var ids = subgraph.Graph.Nodes.ToDictionary(n => n.Id, n => Combine(node.Id, n.Id));
            PortAddress Remap(PortAddress port) => new(ids[port.Node], port.Port);
            var position = document.Layout.FirstOrDefault(l => l.Node == node.Id);
            var nested = subgraph.Graph with
            {
                Nodes = subgraph.Graph.Nodes.Select(n => MapDevices(n with { Id = ids[n.Id] }, subgraph.Devices ?? [])).ToArray(),
                Connections = subgraph.Graph.Connections.Select(c => new PipelineConnection(Remap(c.Source), Remap(c.Target))).ToArray(),
                Layout = subgraph.Graph.Layout.Select(l => new NodeLayout(ids[l.Node], l.X + (position?.X ?? 0), l.Y + (position?.Y ?? 0))).ToArray()
            };
            var expanded = ExpandCore(nested, depth + 1);
            nodes.AddRange(expanded.Document.Nodes);
            connections.AddRange(expanded.Document.Connections);
            layout.AddRange(expanded.Document.Layout);
            foreach (var port in subgraph.Inputs)
            {
                var target = Remap(port.Address);
                var input = expanded.Inputs.GetValueOrDefault(target, target);
                inputs.Add(new(node.Id, port.Id), input);
                if (port.Kind == PortValueKind.Numeric && port.Parameter is { } parameter &&
                    !document.Connections.Any(c => c.Target == new PortAddress(node.Id, port.Id)))
                {
                    var value = NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(parameter.DefaultValue), Combine(node.Id, input.Node));
                    // Each exposed input gets its own stable default node, even on the same inner processor.
                    value = value with
                    {
                        Id = Combine(value.Id, new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(port.Id))[..16]))
                    };
                    nodes.Add(value);
                    connections.Add(new(new(value.Id, "value"), input));
                }
            }
            foreach (var port in subgraph.Outputs)
            {
                var source = Remap(port.Address);
                outputs.Add(new(node.Id, port.Id), expanded.Outputs.GetValueOrDefault(source, source));
            }
        }
        connections.AddRange(document.Connections.Select(c => new PipelineConnection(outputs.GetValueOrDefault(c.Source, c.Source), inputs.GetValueOrDefault(c.Target, c.Target))));
        if (nodes.Count > 1000 || connections.Count > 8000)
            throw new PipelineValidationException([new("Pipeline.Error.Document")]);
        return new(document with
        {
            Nodes = nodes.ToArray(),
            Connections = connections.ToArray(),
            Layout = layout.ToArray()
        }, inputs, outputs);
    }
    private static Guid Combine(Guid parent, Guid child)
    {
        Span<byte> source = stackalloc byte[32];
        parent.TryWriteBytes(source);
        child.TryWriteBytes(source[16..]);
        return new Guid(SHA256.HashData(source)[..16]);
    }
}
