using System.Text.Json;

namespace YokoWave.Pipelines;

internal static class PipelineDocumentValidation
{
    public static void EnsureValid(PipelineDocument document)
    {
        if (document is null || document.Version != 1 || document.Id == Guid.Empty || string.IsNullOrWhiteSpace(document.Name) || document.Name.Length > PipelineDocument.MaximumNameLength ||
            !double.IsFinite(document.DurationSeconds) || document.DurationSeconds is < 1 or > 7200 ||
            document.Nodes is null || document.Connections is null || document.Layout is null || document.Nodes.Length > 1000 || document.Connections.Length > 8000 ||
            document.Nodes.Any(n => n is null || n.Id == Guid.Empty || n.Configuration.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) ||
            document.Nodes.Select(n => n.Id).Distinct().Count() != document.Nodes.Length ||
            document.Connections.Any(c => c?.Source is null || c.Target is null) ||
            document.AssignedDevices is { } assigned && (assigned.Length > 256 || assigned.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.StartsWith('$')) ||
                assigned.Distinct(StringComparer.Ordinal).Count() != assigned.Length) ||
            document.Layout.Any(l => l is null || !double.IsFinite(l.X) || !double.IsFinite(l.Y) || !document.Nodes.Any(n => n.Id == l.Node)))
        {
            throw new PipelineValidationException([new("Pipeline.Error.Document")]);
        }

        EnsurePorts(document, document.Inputs ?? [], PortDirection.Input);
        EnsurePorts(document, document.Outputs ?? [], PortDirection.Output);
    }

    public static void EnsurePorts(PipelineDocument document, IReadOnlyList<ExposedPort> ports, PortDirection direction)
    {
        if (ports.Count > 8000 || ports.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.NameKey) ||
            p.Address is null || string.IsNullOrWhiteSpace(p.Address.Port) || !document.Nodes.Any(n => n.Id == p.Address.Node) || !Enum.IsDefined(p.Kind) ||
            p.Parameter is { } spec && (!double.IsFinite(spec.Minimum) || !double.IsFinite(spec.Maximum) || spec.Minimum > spec.Maximum || !spec.Accepts(spec.DefaultValue))) ||
            ports.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != ports.Count ||
            direction == PortDirection.Input && ports.Select(p => p.Address).Distinct().Count() != ports.Count)
        {
            throw new PipelineValidationException([new("Pipeline.Error.Connection")]);
        }
    }

    public static void ValidateInterfaces(PipelineDocument document, NodeTypeRegistry registry, List<PipelineDiagnostic> errors)
    {
        ValidatePorts(document, document.Inputs ?? [], PortDirection.Input, registry, errors);
        ValidatePorts(document, document.Outputs ?? [], PortDirection.Output, registry, errors);
        foreach (var node in document.Nodes.Where(n => n.Type == SubgraphExpansion.Type))
        {
            var subgraph = node.Configuration.Deserialize<SubgraphNode>(PipelineJson.Options)!;
            ValidatePorts(subgraph.Graph, subgraph.Inputs, PortDirection.Input, registry, errors);
            ValidatePorts(subgraph.Graph, subgraph.Outputs, PortDirection.Output, registry, errors);
            ValidateInterfaces(subgraph.Graph, registry, errors);
        }
    }

    private static void ValidatePorts(PipelineDocument document, IEnumerable<ExposedPort> ports, PortDirection direction,
        NodeTypeRegistry registry, List<PipelineDiagnostic> errors)
    {
        foreach (var port in ports)
        {
            var node = document.Nodes.Single(n => n.Id == port.Address.Node);
            if (node.Type is null || !registry.TryGet(node.Type, out var registration))
            {
                errors.Add(new("Pipeline.Error.UnknownNode", node.Id));
                continue;
            }
            try
            {
                var actual = registration.PortsFor(registration.Read(node.Configuration))
                    .SingleOrDefault(p => p.Id == port.Address.Port && p.Direction == direction);
                if (actual is null || actual.Kind != port.Kind)
                    errors.Add(new("Pipeline.Error.Connection", node.Id, port.Address.Port));
                else if (direction == PortDirection.Input && port.Parameter is { } parameter && actual.Parameter?.Accepts(parameter.DefaultValue) == false)
                    errors.Add(new("Pipeline.Error.Range", node.Id, port.Address.Port));
            }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            {
                errors.Add(new("Pipeline.Error.Configuration", node.Id));
            }
        }
    }
}
