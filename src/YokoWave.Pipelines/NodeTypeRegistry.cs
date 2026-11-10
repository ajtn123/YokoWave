using System.Text.Json;
using YokoWave.Core;

namespace YokoWave.Pipelines;

public interface ISignalProcessor
{
    void Evaluate(NodeEvaluationContext context);
}

public abstract class NodeRegistration(string type, string nameKey, NodePort[] ports)
{
    public string Type { get; } = type;
    public string NameKey { get; } = nameKey;
    public IReadOnlyList<NodePort> Ports { get; } = Array.AsReadOnly(ports);
    public abstract object Read(JsonElement configuration);
    public abstract bool Validate(object configuration);
    public abstract ISignalProcessor Create(object configuration, IReadOnlyDictionary<Guid, WaveformAsset> waveforms);
    public virtual DeviceOutputBinding? Binding(object configuration) => null;
    public virtual IReadOnlyList<DeviceOutputBinding> Resources(object configuration) => Binding(configuration) is { } binding ? [binding] : [];
    public virtual IRunParticipant? Participant(NodeRunContext context, object configuration) => null;
    public virtual IReadOnlyList<NodePort> PortsFor(object configuration) => Ports;
    public virtual IEnumerable<Guid> WaveformReferences(object configuration) => [];
    public virtual bool IsService => false;
    public virtual bool AcceptsDevice(object configuration, DeviceDescriptor device) => true;
}

public sealed class NodeRegistration<T>(string type, string nameKey, NodePort[] ports, Func<T, bool> validate,
    Func<T, IReadOnlyDictionary<Guid, WaveformAsset>, ISignalProcessor> factory,
    Func<T, DeviceOutputBinding?>? binding = null,
    Func<NodeRunContext, T, IRunParticipant?>? participant = null,
    Func<T, IReadOnlyList<NodePort>>? dynamicPorts = null,
    Func<T, IReadOnlyList<DeviceOutputBinding>>? resources = null,
    Func<T, IEnumerable<Guid>>? waveformReferences = null,
    bool service = false,
    Func<T, DeviceDescriptor, bool>? acceptsDevice = null) : NodeRegistration(type, nameKey, ports) where T : class
{
    public override object Read(JsonElement configuration) => configuration.Deserialize<T>(PipelineJson.Options) ?? throw new JsonException();
    public override bool Validate(object configuration) => validate((T)configuration);
    public override ISignalProcessor Create(object configuration, IReadOnlyDictionary<Guid, WaveformAsset> waveforms) => factory((T)configuration, waveforms);
    public override DeviceOutputBinding? Binding(object configuration) => binding?.Invoke((T)configuration);
    public override IRunParticipant? Participant(NodeRunContext context, object configuration) => participant?.Invoke(context, (T)configuration);
    public override IReadOnlyList<NodePort> PortsFor(object configuration) => dynamicPorts?.Invoke((T)configuration) ?? Ports;
    public override IReadOnlyList<DeviceOutputBinding> Resources(object configuration) => resources?.Invoke((T)configuration) ?? base.Resources(configuration);
    public override IEnumerable<Guid> WaveformReferences(object configuration) => waveformReferences?.Invoke((T)configuration) ?? [];
    public override bool IsService => service;
    public override bool AcceptsDevice(object configuration, DeviceDescriptor device) => acceptsDevice?.Invoke((T)configuration, device) ?? true;
}

public sealed class NodeTypeRegistry
{
    private readonly Dictionary<string, NodeRegistration> _types = new(StringComparer.Ordinal);
    public IReadOnlyCollection<NodeRegistration> Types => _types.Values;
    public NodeTypeRegistry Register(NodeRegistration registration)
    {
        _types.Add(registration.Type, registration);
        return this;
    }
    public bool TryGet(string type, out NodeRegistration registration) => _types.TryGetValue(type, out registration!);
    public NodeRegistration Get(string type) => _types[type];
}

public enum OutputBindingKind
{
    Channel, Pump, Native, Router, Sensor, Command, Export, ControlEndpoint
}
public sealed record DeviceOutputBinding(string DeviceId, OutputBindingKind Kind, int Channel = 0)
{
    private bool IsOutput => Kind is OutputBindingKind.Channel or OutputBindingKind.Pump or OutputBindingKind.Native or OutputBindingKind.Router;
    public bool ConflictsWith(DeviceOutputBinding other) => DeviceId == other.DeviceId &&
        (Kind == OutputBindingKind.Export || other.Kind == OutputBindingKind.Export ||
         IsOutput && other.IsOutput && (Kind != OutputBindingKind.Channel || other.Kind != OutputBindingKind.Channel || Channel == other.Channel));
}
public sealed record ConstantNode(double Value = 0);
public sealed record WaveformNode(Guid WaveformId);
public sealed record GainNode(double Gain = 1);
public sealed record LimitNode(double Limit = .6);
public sealed record ChannelOutputNode(string DeviceId = "", int Channel = 0, double Limit = .6);
public sealed record PumpOutputNode(string DeviceId = "", double Limit = 1);

public static partial class BuiltInNodes
{
    public const string Constant = "numeric.constant";
    public const string Waveform = "waveform.player";
    public const string Gain = "numeric.gain";
    public const string Limit = "numeric.limit";
    public const string ChannelOutput = "device.channel";
    public const string PumpOutput = "device.pump";

    public static NodeTypeRegistry CreateRegistry() => RegisterNumeric(RegisterFeatures(new NodeTypeRegistry()
        .Register(new NodeRegistration<ConstantNode>(Constant, "Pipeline.Node.Constant",
            [NodePort.Output("value", "Pipeline.Port.Value")], c => double.IsFinite(c.Value), (c, _) => new ConstantProcessor(c.Value)))
        .Register(new NodeRegistration<WaveformNode>(Waveform, "Pipeline.Node.Waveform",
            WaveformPorts.Create(), c => c.WaveformId != Guid.Empty,
            (c, waves) => new WaveformProcessor(waves[c.WaveformId]), waveformReferences: c => [c.WaveformId]))
        .Register(new NodeRegistration<GainNode>(Gain, "Pipeline.Node.Gain",
            [NodePort.Input("value", "Pipeline.Port.Value", double.MinValue, double.MaxValue),
             NodePort.Input("gain", "Pipeline.Port.Gain", -1000, 1000, 1), NodePort.Output("value", "Pipeline.Port.Value")],
            c => double.IsFinite(c.Gain) && c.Gain is >= -1000 and <= 1000, (c, _) => new GainProcessor(c.Gain)))
        .Register(new NodeRegistration<LimitNode>(Limit, "Pipeline.Node.Limit",
            [NodePort.Input("value", "Pipeline.Port.Value", double.MinValue, double.MaxValue),
             NodePort.Input("limit", "Pipeline.Port.Limit", 0, 1, .6, "ratio"), NodePort.Output("value", "Pipeline.Port.Value")],
            c => double.IsFinite(c.Limit) && c.Limit is >= 0 and <= 1, (c, _) => new LimitProcessor(c.Limit)))
        .Register(new NodeRegistration<ChannelOutputNode>(ChannelOutput, "Pipeline.Node.ChannelOutput",
            [NodePort.Input("strength", "Pipeline.Port.Strength", 0, 1, 0, "ratio"),
             NodePort.Input("frequency", "Pipeline.Port.Frequency", 1, 100, 20, "Hz"),
             NodePort.Input("pulseWidth", "Pipeline.Port.PulseWidth", 0, 1, 1, "ratio"),
             NodePort.Input("limit", "Pipeline.Port.Limit", 0, 1, 1, "ratio")],
            c => c.DeviceId is { Length: > 0 and <= 512 } && c.Channel is >= 0 and <= 2 && ValidLimit(c.Limit),
            (c, _) => new ChannelOutputProcessor(c), c => new(c.DeviceId, OutputBindingKind.Channel, c.Channel)))
        .Register(new NodeRegistration<PumpOutputNode>(PumpOutput, "Pipeline.Node.PumpOutput",
            [NodePort.Input("direction", "Pipeline.Port.Direction", 0, 2, integer: true),
             NodePort.Input("water", "Pipeline.Port.Water", 0, 1, integer: true),
             NodePort.Input("peristalticSeconds", "Pipeline.Port.PeristalticSeconds", 0, 65535, 0, "s", true),
             NodePort.Input("waterSeconds", "Pipeline.Port.WaterSeconds", 0, 65535, 0, "s", true)],
            c => c.DeviceId is { Length: > 0 and <= 512 } && ValidLimit(c.Limit), (c, _) => new PumpOutputProcessor(c), c => new(c.DeviceId, OutputBindingKind.Pump)))));
}
