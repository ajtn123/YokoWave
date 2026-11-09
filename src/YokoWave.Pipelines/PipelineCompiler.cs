using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Pipelines;

public sealed record PipelineDiagnostic(string Key, Guid? Node = null, string? Port = null)
{
    public string Message => L10n.Get(Key);
}

public sealed class PipelineValidationException(IReadOnlyList<PipelineDiagnostic> diagnostics) : Exception
{
    public IReadOnlyList<PipelineDiagnostic> Diagnostics { get; } = diagnostics;
    public override string Message => string.Join(Environment.NewLine, Diagnostics.Select(d => d.Message));
}

internal sealed record CompiledNode(Guid Id, NodeRegistration Registration, object Configuration);

public sealed class PipelinePlan
{
    // The execution definition is private; callers cannot change a compiled graph while it is running.
    internal PipelineDocument Document { get; }
    internal CompiledNode[] Nodes { get; }
    internal Dictionary<PortAddress, PortAddress> Inputs { get; }
    internal Dictionary<Guid, WaveformAsset> Waveforms { get; }
    public IReadOnlyList<string> DeviceIds { get; }
    public IReadOnlyList<string> OutputDeviceIds { get; }
    public IReadOnlyList<DeviceOutputBinding> Bindings { get; }
    public Guid Id => Document.Id;
    public string Name => Document.Name;
    public TimeSpan? Duration => Document.TimerEnabled ? TimeSpan.FromSeconds(Document.DurationSeconds) : null;
    internal PipelinePlan(PipelineDocument document, CompiledNode[] nodes, Dictionary<PortAddress, PortAddress> inputs,
        Dictionary<Guid, WaveformAsset> waveforms, DeviceOutputBinding[] bindings)
    {
        Document = document;
        Nodes = nodes;
        Inputs = inputs;
        Waveforms = waveforms;
        Bindings = Array.AsReadOnly(bindings);
        DeviceIds = Array.AsReadOnly(bindings.Select(b => b.DeviceId).Distinct(StringComparer.Ordinal).ToArray());
        OutputDeviceIds = Array.AsReadOnly(bindings.Where(b => b.Kind is OutputBindingKind.Channel or OutputBindingKind.Pump or OutputBindingKind.Native or OutputBindingKind.Router)
            .Select(b => b.DeviceId).Distinct(StringComparer.Ordinal).ToArray());
    }
}

public sealed class PipelineCompiler(NodeTypeRegistry registry)
{
    public IReadOnlyList<PipelineDiagnostic> Validate(PipelineDocument document, IEnumerable<WaveformAsset> waveforms,
        IReadOnlyDictionary<string, DeviceDescriptor>? devices = null)
    {
        return CompileCore(document, waveforms, devices).Errors;
    }

    public PipelinePlan Compile(PipelineDocument document, IEnumerable<WaveformAsset> waveforms,
        IReadOnlyDictionary<string, DeviceDescriptor>? devices = null)
    {
        var result = CompileCore(document, waveforms, devices);
        if (result.Errors.Count > 0)
            throw new PipelineValidationException(result.Errors);
        return result.Plan!;
    }

    internal static DeviceOutputBinding[] ValidateResources(IEnumerable<CompiledNode> source, IReadOnlyDictionary<string, DeviceDescriptor>? devices, List<PipelineDiagnostic> errors)
    {
        var nodes = source.ToArray();
        List<DeviceOutputBinding> bindings = [];
        foreach (var node in nodes)
        {
            foreach (var binding in node.Registration.Resources(node.Configuration))
            {
                if (bindings.Any(b => b.ConflictsWith(binding)))
                    errors.Add(new("Pipeline.Error.DuplicateOutput", node.Id));
                bindings.Add(binding);
                if (devices is not null && (!devices.TryGetValue(binding.DeviceId, out var device) || !Accepts(binding, device) || !AcceptsConfiguration(node.Configuration, device)))
                    errors.Add(new("Pipeline.Error.Device", node.Id));
            }
        }
        return bindings.ToArray();
    }
    public static bool Accepts(DeviceOutputBinding binding, DeviceDescriptor device) => device.IsSupported && binding.Kind switch
    {
        OutputBindingKind.Sensor => device.IsInput || device.Profile?.Motion == true || device.Profile?.Supports(DeviceCapability.SensorStream) == true ||
            device.Profile?.Supports(DeviceCapability.StatusQuery) == true || device.Simulated == true || device.ProtocolId is null,
        OutputBindingKind.Command => true,
        OutputBindingKind.Export => device.Origin == DeviceOrigin.Local,
        OutputBindingKind.Pump => device.Kind == DeviceKind.Pump,
        OutputBindingKind.Native => !device.IsInput && device.Kind != DeviceKind.Pump,
        OutputBindingKind.Router => !device.IsInput,
        OutputBindingKind.ControlEndpoint => device.Origin == DeviceOrigin.Local,
        _ => !device.IsInput && device.Kind != DeviceKind.Pump && device.HasChannel(binding.Channel)
    };

    private (PipelinePlan? Plan, List<PipelineDiagnostic> Errors) CompileCore(PipelineDocument source,
        IEnumerable<WaveformAsset> waveforms, IReadOnlyDictionary<string, DeviceDescriptor>? devices)
    {
        List<PipelineDiagnostic> errors = [];
        PipelineDocument document;
        try
        {
            var snapshot = PipelineJson.Snapshot(source);
            document = SubgraphExpansion.Expand(snapshot);
            PipelineDocumentValidation.ValidateInterfaces(snapshot, registry, errors);
        }
        catch (PipelineValidationException error)
        {
            return (null, error.Diagnostics.ToList());
        }
        catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException)
        {
            return (null, [new("Pipeline.Error.Connection")]);
        }
        var waves = waveforms.Where(w => w is not null && w.TryValidate(out _)).ToDictionary(w => w.Id, w => w with
        {
            Points = w.Points?.ToArray(),
            Samples = w.Samples?.Select(s => s with { Channels = s.Channels?.ToArray() }).ToArray()
        });
        var nodes = ReadNodes(document, waves, errors);
        var inputs = ConnectInputs(document, nodes, errors);
        var ordered = SortNodes(nodes, inputs, errors);
        var bindings = ValidateResources(nodes.Values, devices, errors);
        if (nodes.Count == 0)
            errors.Add(new("Pipeline.Error.NoOutput"));
        return errors.Count > 0 ? (null, errors) : (new(document, ordered, inputs, waves, bindings), errors);
    }

    private Dictionary<Guid, CompiledNode> ReadNodes(PipelineDocument document, IReadOnlyDictionary<Guid, WaveformAsset> waves, List<PipelineDiagnostic> errors)
    {
        Dictionary<Guid, CompiledNode> nodes = [];
        foreach (var node in document.Nodes)
        {
            if (node is null || node.Id == Guid.Empty || nodes.ContainsKey(node.Id))
            {
                errors.Add(new("Pipeline.Error.NodeId", node?.Id));
                continue;
            }
            if (node.Type is null || !registry.TryGet(node.Type, out var registration))
            {
                errors.Add(new("Pipeline.Error.UnknownNode", node.Id));
                continue;
            }
            try
            {
                object config = registration.Read(node.Configuration);
                if (!registration.Validate(config))
                {
                    errors.Add(new("Pipeline.Error.Configuration", node.Id));
                    continue;
                }
                if (registration.WaveformReferences(config).Any(id => !waves.ContainsKey(id)))
                    errors.Add(new("Pipeline.Error.Waveform", node.Id));
                nodes.Add(node.Id, new(node.Id, registration, config));
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                errors.Add(new("Pipeline.Error.Configuration", node.Id));
            }
        }
        return nodes;
    }

    private static Dictionary<PortAddress, PortAddress> ConnectInputs(PipelineDocument document, IReadOnlyDictionary<Guid, CompiledNode> nodes, List<PipelineDiagnostic> errors)
    {
        Dictionary<PortAddress, PortAddress> inputs = [];
        foreach (var connection in document.Connections)
        {
            if (connection?.Source is not { } from || connection.Target is not { } to ||
                !nodes.TryGetValue(from.Node, out var sourceNode) || !nodes.TryGetValue(to.Node, out var targetNode) ||
                !sourceNode.Registration.PortsFor(sourceNode.Configuration).Any(p => p.Id == from.Port && p.Direction == PortDirection.Output) ||
                !targetNode.Registration.PortsFor(targetNode.Configuration).Any(p => p.Id == to.Port && p.Direction == PortDirection.Input))
            {
                errors.Add(new("Pipeline.Error.Connection", connection?.Target?.Node, connection?.Target?.Port));
                continue;
            }
            if (!inputs.TryAdd(to, from))
                errors.Add(new("Pipeline.Error.MultipleSources", to.Node, to.Port));
            if (sourceNode.Registration.PortsFor(sourceNode.Configuration).Single(p => p.Id == from.Port && p.Direction == PortDirection.Output).Kind !=
                targetNode.Registration.PortsFor(targetNode.Configuration).Single(p => p.Id == to.Port && p.Direction == PortDirection.Input).Kind)
            {
                errors.Add(new("Pipeline.Error.Connection", to.Node, to.Port));
            }

            if (sourceNode.Configuration is ConstantNode constant &&
                targetNode.Registration.PortsFor(targetNode.Configuration).Single(p => p.Id == to.Port && p.Direction == PortDirection.Input).Parameter?.Accepts(constant.Value) == false)
            {
                errors.Add(new("Pipeline.Error.Range", to.Node, to.Port));
            }
        }
        return inputs;
    }

    private static CompiledNode[] SortNodes(IReadOnlyDictionary<Guid, CompiledNode> nodes, IReadOnlyDictionary<PortAddress, PortAddress> inputs, List<PipelineDiagnostic> errors)
    {
        var degrees = nodes.Keys.ToDictionary(id => id, _ => 0);
        var downstream = nodes.Keys.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var (to, from) in inputs)
        {
            degrees[to.Node]++;
            downstream[from.Node].Add(to.Node);
        }
        Queue<Guid> ready = new(degrees.Where(p => p.Value == 0).Select(p => p.Key));
        List<CompiledNode> ordered = [];
        while (ready.TryDequeue(out var id))
        {
            ordered.Add(nodes[id]);
            foreach (var next in downstream[id])
            {
                if (--degrees[next] == 0)
                    ready.Enqueue(next);
            }
        }
        if (ordered.Count != nodes.Count)
            errors.Add(new("Pipeline.Error.Cycle"));
        return ordered.ToArray();
    }

    private static bool AcceptsConfiguration(object config, DeviceDescriptor device) => config switch
    {
        NativeOutputNode native => (native.Settings ?? new()).Accepts(device),
        CommandNode command => device.Profile?.Supports(command.Command.Capability) == true,
        _ => true
    };
}
