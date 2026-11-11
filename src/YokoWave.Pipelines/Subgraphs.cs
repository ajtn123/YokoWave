using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YokoWave.Pipelines;

public sealed record ExposedPort(string Id, string NameKey, PortAddress Address, NumericParameterSpec? Parameter = null, PortValueKind Kind = PortValueKind.Numeric);
public sealed record SubgraphNode(PipelineDocument Graph, ExposedPort[] Inputs, ExposedPort[] Outputs,
    Dictionary<string, string>? Devices = null);
public sealed record SavedPipelineTemplate(Guid Id, string Name, PipelineDocument Graph, ExposedPort[] Inputs, ExposedPort[] Outputs);

/// <summary>Expands copied graph definitions into one DAG and one resource reservation.</summary>
public static class SubgraphExpansion
{
    public const string Type = "graph.subgraph";

    public static NodeRegistration Registration() => new NodeRegistration<SubgraphNode>(Type, "Pipeline.Node.Subgraph", [],
        ValidConfiguration,
        (_, _) => new NoopProcessor(), dynamicPorts: c =>
            c.Inputs.Select(p => new NodePort(p.Id, p.NameKey, PortDirection.Input,
                p.Kind == PortValueKind.Numeric ? p.Parameter ?? new("", double.MinValue, double.MaxValue, 0) : null, p.Kind))
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
                    if (key == "DeviceId" && item is JsonValue scalar && scalar.TryGetValue<string>(out string? id) && devices.TryGetValue(id, out string? mapped))
                    {
                        obj[key] = mapped;
                    }
                    else if (key == "DeviceIds" && item is JsonArray array)
                    {
                        for (int i = 0; i < array.Count; i++)
                        {
                            if (array[i] is JsonValue element && element.TryGetValue<string>(out string? original) && devices.TryGetValue(original, out string? replacement))
                                array[i] = replacement;
                        }
                    }
                    else if (key == "Devices" && item is JsonObject map)
                    {
                        foreach (var entry in map.ToArray())
                        {
                            if (entry.Value is JsonValue original && original.TryGetValue<string>(out string? target) && devices.TryGetValue(target, out string? replacement))
                                map[entry.Key] = replacement;
                        }
                    }
                    else
                    {
                        Visit(item);
                    }
                }
            }
            else if (value is JsonArray array)
            {
                foreach (var item in array)
                    Visit(item);
            }
        }
        Visit(json);
        return node with
        {
            Configuration = JsonSerializer.SerializeToElement(json, PipelineJson.Options)
        };
    }

    public static PipelineDocument Expand(PipelineDocument document)
    {
        var expanded = ExpandCore(document, 0);
        var defaults = expanded.Defaults.Where(p => !expanded.Document.Connections.Any(c => c.Target == p.Key)).ToArray();
        var result = expanded.Document with
        {
            Nodes = [.. expanded.Document.Nodes, .. defaults.Select(p => NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(p.Value.Value), p.Value.Id))],
            Connections = [.. expanded.Document.Connections, .. defaults.Select(p => new PipelineConnection(new(p.Value.Id, "value"), p.Key))]
        };
        PipelineDocumentValidation.EnsureValid(result);
        return result;
    }

    private static bool ValidConfiguration(SubgraphNode configuration)
    {
        if (configuration.Graph is null || configuration.Inputs is null || configuration.Outputs is null)
            return false;
        try
        {
            PipelineDocumentValidation.EnsureValid(configuration.Graph);
            PipelineDocumentValidation.EnsurePorts(configuration.Graph, configuration.Inputs, PortDirection.Input);
            PipelineDocumentValidation.EnsurePorts(configuration.Graph, configuration.Outputs, PortDirection.Output);
            return true;
        }
        catch (PipelineValidationException)
        {
            return false;
        }
    }
    private static Expansion ExpandCore(PipelineDocument document, int depth)
    {
        if (depth > 16)
            throw new PipelineValidationException([new("Pipeline.Error.SubgraphDepth")]);
        PipelineDocumentValidation.EnsureValid(document);
        List<NodeDefinition> nodes = [];
        List<PipelineConnection> connections = [];
        List<NodeLayout> layout = [];
        Dictionary<PortAddress, PortAddress> inputs = [], outputs = [];
        Dictionary<PortAddress, InputDefault> defaults = [];
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
            PipelineDocumentValidation.EnsureValid(subgraph.Graph);
            PipelineDocumentValidation.EnsurePorts(subgraph.Graph, subgraph.Inputs, PortDirection.Input);
            PipelineDocumentValidation.EnsurePorts(subgraph.Graph, subgraph.Outputs, PortDirection.Output);
            var ids = subgraph.Graph.Nodes.ToDictionary(n => n.Id, n => Combine(node.Id, n.Id));
            PortAddress Remap(PortAddress port) => new(ids[port.Node], port.Port);
            var position = document.Layout.FirstOrDefault(l => l.Node == node.Id);
            var nested = subgraph.Graph with
            {
                Nodes = subgraph.Graph.Nodes.Select(n => MapDevices(n with { Id = ids[n.Id] }, subgraph.Devices ?? [])).ToArray(),
                Connections = subgraph.Graph.Connections.Select(c => new PipelineConnection(Remap(c.Source), Remap(c.Target))).ToArray(),
                Layout = subgraph.Graph.Layout.Select(l => new NodeLayout(ids[l.Node], l.X + (position?.X ?? 0), l.Y + (position?.Y ?? 0))).ToArray(),
                Inputs = subgraph.Graph.Inputs?.Select(p => p with { Address = Remap(p.Address) }).ToArray(),
                Outputs = subgraph.Graph.Outputs?.Select(p => p with { Address = Remap(p.Address) }).ToArray()
            };
            var expanded = ExpandCore(nested, depth + 1);
            nodes.AddRange(expanded.Document.Nodes);
            connections.AddRange(expanded.Document.Connections);
            layout.AddRange(expanded.Document.Layout);
            foreach (var entry in expanded.Defaults)
                defaults.Add(entry.Key, entry.Value);
            foreach (var port in subgraph.Inputs)
            {
                var target = Remap(port.Address);
                var input = expanded.Inputs.GetValueOrDefault(target, target);
                inputs.Add(new(node.Id, port.Id), input);
                if (document.Connections.Any(c => c.Target == new PortAddress(node.Id, port.Id)))
                {
                    connections.RemoveAll(c => c.Target == input);
                    defaults.Remove(input);
                }
                else if (port.Kind == PortValueKind.Numeric && port.Parameter is { } parameter && !connections.Any(c => c.Target == input))
                {
                    // Defer defaults until all enclosing templates have resolved their input overrides.
                    var id = Combine(Combine(node.Id, input.Node), new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(port.Id))[..16]));
                    defaults[input] = new(id, parameter.DefaultValue);
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
            Layout = layout.ToArray(),
            Inputs = document.Inputs?.Select(p => p with { Address = inputs.GetValueOrDefault(p.Address, p.Address) }).ToArray(),
            Outputs = document.Outputs?.Select(p => p with { Address = outputs.GetValueOrDefault(p.Address, p.Address) }).ToArray()
        }, inputs, outputs, defaults);
    }
    private static Guid Combine(Guid parent, Guid child)
    {
        Span<byte> source = stackalloc byte[32];
        parent.TryWriteBytes(source);
        child.TryWriteBytes(source[16..]);
        return new Guid(SHA256.HashData(source)[..16]);
    }
    private sealed class NoopProcessor : ISignalProcessor
    {
        public void Evaluate(NodeEvaluationContext context)
        {
        }
    }
    private sealed record InputDefault(Guid Id, double Value);
    private sealed record Expansion(PipelineDocument Document, Dictionary<PortAddress, PortAddress> Inputs,
        Dictionary<PortAddress, PortAddress> Outputs, Dictionary<PortAddress, InputDefault> Defaults);
}
