using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Core.Bluetooth;

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
    internal PipelinePlan(PipelineDocument document, CompiledNode[] nodes, Dictionary<PortAddress, PortAddress> inputs,
        Dictionary<Guid, WaveformPreset> waveforms, DeviceOutputBinding[] bindings)
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
    // The execution definition is private; callers cannot change a compiled graph while it is running.
    internal PipelineDocument Document { get; }
    internal CompiledNode[] Nodes { get; }
    internal Dictionary<PortAddress, PortAddress> Inputs { get; }
    internal Dictionary<Guid, WaveformPreset> Waveforms { get; }
    public IReadOnlyList<string> DeviceIds { get; }
    public IReadOnlyList<string> OutputDeviceIds { get; }
    public IReadOnlyList<DeviceOutputBinding> Bindings { get; }
    public Guid Id => Document.Id;
    public string Name => Document.Name;
    public TimeSpan? Duration => Document.TimerEnabled ? TimeSpan.FromSeconds(Document.DurationSeconds) : null;
}

public sealed class PipelineCompiler(NodeTypeRegistry registry)
{
    public IReadOnlyList<PipelineDiagnostic> Validate(PipelineDocument document, IEnumerable<WaveformPreset> waveforms,
        IReadOnlyDictionary<string, DeviceDescriptor>? devices = null)
    {
        return CompileCore(document, waveforms, devices).Errors;
    }

    public PipelinePlan Compile(PipelineDocument document, IEnumerable<WaveformPreset> waveforms,
        IReadOnlyDictionary<string, DeviceDescriptor>? devices = null)
    {
        var result = CompileCore(document, waveforms, devices);
        if (result.Errors.Count > 0)
            throw new PipelineValidationException(result.Errors);
        return result.Plan!;
    }

    private (PipelinePlan? Plan, List<PipelineDiagnostic> Errors) CompileCore(PipelineDocument source,
        IEnumerable<WaveformPreset> waveforms, IReadOnlyDictionary<string, DeviceDescriptor>? devices)
    {
        List<PipelineDiagnostic> errors = [];
        if (source.Version != 1 || source.Id == Guid.Empty || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 60 ||
            !double.IsFinite(source.DurationSeconds) || source.DurationSeconds is < 1 or > 7200 ||
            source.Nodes is null || source.Connections is null || source.Layout is null || source.Nodes.Length > 1000 || source.Connections.Length > 8000 ||
            source.Nodes.Any(n => n is null || n.Configuration.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) ||
            source.Layout.Any(l => l is null || !double.IsFinite(l.X) || !double.IsFinite(l.Y)))
            return (null, [new("Pipeline.Error.Document")]);
        PipelineDocument document;
        try
        {
            document = SubgraphExpansion.Expand(PipelineJson.Snapshot(source));
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

    private Dictionary<Guid, CompiledNode> ReadNodes(PipelineDocument document, IReadOnlyDictionary<Guid, WaveformPreset> waves, List<PipelineDiagnostic> errors)
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
                var config = registration.Read(node.Configuration);
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
                errors.Add(new("Pipeline.Error.Connection", to.Node, to.Port));
            if (sourceNode.Configuration is ConstantNode constant &&
                targetNode.Registration.PortsFor(targetNode.Configuration).Single(p => p.Id == to.Port && p.Direction == PortDirection.Input).Parameter?.Accepts(constant.Value) == false)
                errors.Add(new("Pipeline.Error.Range", to.Node, to.Port));
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
                if (--degrees[next] == 0)
                    ready.Enqueue(next);
        }
        if (ordered.Count != nodes.Count)
            errors.Add(new("Pipeline.Error.Cycle"));
        return ordered.ToArray();
    }

    internal static DeviceOutputBinding[] ValidateResources(IEnumerable<CompiledNode> source, IReadOnlyDictionary<string, DeviceDescriptor>? devices, List<PipelineDiagnostic> errors)
    {
        var nodes = source.ToArray();
        List<DeviceOutputBinding> bindings = [];
        var channelOptions = nodes.Where(n => n.Configuration is ChannelOutputNode).Select(n => (ChannelOutputNode)n.Configuration).GroupBy(n => n.DeviceId);
        foreach (var group in channelOptions)
            if (group.Select(n => (n.Gentle, Hardware: n.Hardware ?? new(), Balances: n.Balances ?? new())).Distinct().Count() > 1)
                errors.Add(new("Pipeline.Error.DeviceOptions"));
        foreach (var node in nodes)
        {
            foreach (var binding in node.Registration.Resources(node.Configuration))
            {
                if (bindings.Any(b => Conflicts(b, binding)))
                    errors.Add(new("Pipeline.Error.DuplicateOutput", node.Id));
                bindings.Add(binding);
                var needsMotion = nodes.Any(n => n.Configuration is SensorNode sensor && sensor.DeviceId == binding.DeviceId ||
                    n.Configuration is CommandNode command && command.DeviceId == binding.DeviceId && command.Command is ConfigureMotionStream { Enabled: true });
                if (devices is not null && (!devices.TryGetValue(binding.DeviceId, out var device) || !Accepts(binding, device) || !AcceptsConfiguration(node.Configuration, device, needsMotion)))
                    errors.Add(new("Pipeline.Error.Device", node.Id));
            }
        }
        return bindings.ToArray();
    }

    private static bool Conflicts(DeviceOutputBinding a, DeviceOutputBinding b)
    {
        if (a.DeviceId != b.DeviceId)
            return false;
        if (a.Kind == OutputBindingKind.Export || b.Kind == OutputBindingKind.Export)
            return true;
        var aOutput = a.Kind is OutputBindingKind.Channel or OutputBindingKind.Pump or OutputBindingKind.Native or OutputBindingKind.Router;
        var bOutput = b.Kind is OutputBindingKind.Channel or OutputBindingKind.Pump or OutputBindingKind.Native or OutputBindingKind.Router;
        return aOutput && bOutput && (a.Kind != OutputBindingKind.Channel || b.Kind != OutputBindingKind.Channel || a.Channel == b.Channel);
    }
    private static bool AcceptsConfiguration(object config, DeviceDescriptor device, bool needsMotion) => config switch
    {
        NativeOutputNode native => (native.Settings ?? new()).Accepts(device) && native.Hardware?.Accepts(device, needsMotion) != false,
        ChannelOutputNode channel => channel.Hardware?.Accepts(device, needsMotion) != false,
        CommandNode command => device.Profile?.Supports(command.Command.Capability) == true,
        _ => true
    };
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
}
