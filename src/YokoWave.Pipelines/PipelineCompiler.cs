using System.Text.Json;
using YokoWave.Core;
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
    internal PipelinePlan(PipelineDocument document, CompiledNode[] nodes, Dictionary<PortAddress, PortAddress> inputs,
        Dictionary<Guid, WaveformPreset> waveforms, DeviceOutputBinding[] bindings)
    {
        Document = document;
        Nodes = nodes;
        Inputs = inputs;
        Waveforms = waveforms;
        Bindings = Array.AsReadOnly(bindings);
        DeviceIds = Array.AsReadOnly(bindings.Select(b => b.DeviceId).Distinct(StringComparer.Ordinal).ToArray());
    }
    // The execution definition is private; callers cannot change a compiled graph while it is running.
    internal PipelineDocument Document { get; }
    internal CompiledNode[] Nodes { get; }
    internal Dictionary<PortAddress, PortAddress> Inputs { get; }
    internal Dictionary<Guid, WaveformPreset> Waveforms { get; }
    public IReadOnlyList<string> DeviceIds { get; }
    public IReadOnlyList<DeviceOutputBinding> Bindings { get; }
    public Guid Id => Document.Id;
    public string Name => Document.Name;
    public double OutputLimit => Document.OutputLimit;
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
            !double.IsFinite(source.OutputLimit) || source.OutputLimit is < 0 or > 1 ||
            !double.IsFinite(source.DurationSeconds) || source.DurationSeconds is < 1 or > 7200 ||
            source.Nodes is null || source.Connections is null || source.Layout is null || source.Nodes.Length > 1000 || source.Connections.Length > 8000 ||
            source.Nodes.Any(n => n is null || n.Configuration.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) ||
            source.Layout.Any(l => l is null || !double.IsFinite(l.X) || !double.IsFinite(l.Y)))
            return (null, [new("Pipeline.Error.Document")]);
        var document = PipelineJson.Snapshot(source);
        var waves = waveforms.Where(w => w is not null && w.TryValidate(out _)).ToDictionary(w => w.Id, w => w with
        {
            Points = w.Points?.ToArray(),
            Samples = w.Samples?.Select(s => s with { Channels = s.Channels?.ToArray() }).ToArray()
        });
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
                    errors.Add(new("Pipeline.Error.Configuration", node.Id));
                if (config is WaveformNode wave && (!waves.TryGetValue(wave.WaveformId, out var preset) || !preset.TryValidate(out _)))
                    errors.Add(new("Pipeline.Error.Waveform", node.Id));
                nodes.Add(node.Id, new(node.Id, registration, config));
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                errors.Add(new("Pipeline.Error.Configuration", node.Id));
            }
        }
        Dictionary<PortAddress, PortAddress> inputs = [];
        foreach (var connection in document.Connections)
        {
            if (connection?.Source is not { } from || connection.Target is not { } to ||
                !nodes.TryGetValue(from.Node, out var sourceNode) || !nodes.TryGetValue(to.Node, out var targetNode) ||
                !sourceNode.Registration.Ports.Any(p => p.Id == from.Port && p.Direction == PortDirection.Output) ||
                !targetNode.Registration.Ports.Any(p => p.Id == to.Port && p.Direction == PortDirection.Input))
            {
                errors.Add(new("Pipeline.Error.Connection", connection?.Target?.Node, connection?.Target?.Port));
                continue;
            }
            if (!inputs.TryAdd(to, from))
                errors.Add(new("Pipeline.Error.MultipleSources", to.Node, to.Port));
            if (sourceNode.Configuration is ConstantNode constant &&
                targetNode.Registration.Ports.Single(p => p.Id == to.Port && p.Direction == PortDirection.Input).Parameter?.Accepts(constant.Value) == false)
                errors.Add(new("Pipeline.Error.Range", to.Node, to.Port));
        }
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
        List<DeviceOutputBinding> bindings = [];
        foreach (var node in nodes.Values)
        {
            if (node.Registration.Binding(node.Configuration) is not { } binding)
                continue;
            if (bindings.Any(b => b.DeviceId == binding.DeviceId && (b.Kind == OutputBindingKind.Pump || binding.Kind == OutputBindingKind.Pump || b.Channel == binding.Channel)))
                errors.Add(new("Pipeline.Error.DuplicateOutput", node.Id));
            bindings.Add(binding);
            if (devices is not null && (!devices.TryGetValue(binding.DeviceId, out var device) || !Accepts(binding, device)))
                errors.Add(new("Pipeline.Error.Device", node.Id));
        }
        if (bindings.Count == 0)
            errors.Add(new("Pipeline.Error.NoOutput"));
        return errors.Count > 0 ? (null, errors) : (new(document, ordered.ToArray(), inputs, waves, bindings.ToArray()), errors);
    }

    public static bool Accepts(DeviceOutputBinding binding, DeviceDescriptor device) => device.IsSupported && !device.IsInput &&
        (binding.Kind == OutputBindingKind.Pump ? device.Kind == DeviceKind.Pump : device.Kind != DeviceKind.Pump && device.HasChannel(binding.Channel));
}
