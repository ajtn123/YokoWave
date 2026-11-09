using System.Text.Json;
using YokoWave.Core;

namespace YokoWave.Pipelines;

public interface ISignalProcessor
{
    void Evaluate(NodeEvaluationContext context);
}

public abstract class NodeRegistration(string type, string nameKey, NumericPort[] ports)
{
    public string Type { get; } = type;
    public string NameKey { get; } = nameKey;
    public IReadOnlyList<NumericPort> Ports { get; } = Array.AsReadOnly(ports);
    public abstract object Read(JsonElement configuration);
    public abstract bool Validate(object configuration);
    public abstract ISignalProcessor Create(object configuration, IReadOnlyDictionary<Guid, WaveformPreset> waveforms);
    public virtual DeviceOutputBinding? Binding(object configuration) => null;
}

public sealed class NodeRegistration<T>(string type, string nameKey, NumericPort[] ports, Func<T, bool> validate,
    Func<T, IReadOnlyDictionary<Guid, WaveformPreset>, ISignalProcessor> factory,
    Func<T, DeviceOutputBinding?>? binding = null) : NodeRegistration(type, nameKey, ports) where T : class
{
    public override object Read(JsonElement configuration) => configuration.Deserialize<T>(PipelineJson.Options) ?? throw new JsonException();
    public override bool Validate(object configuration) => validate((T)configuration);
    public override ISignalProcessor Create(object configuration, IReadOnlyDictionary<Guid, WaveformPreset> waveforms) => factory((T)configuration, waveforms);
    public override DeviceOutputBinding? Binding(object configuration) => binding?.Invoke((T)configuration);
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

public enum OutputBindingKind { Channel, Pump }
public sealed record DeviceOutputBinding(string DeviceId, OutputBindingKind Kind, int Channel = 0);
public sealed record ConstantNode(double Value = 0);
public sealed record WaveformNode(Guid WaveformId, int Channel = 0);
public sealed record GainNode(double Gain = 1);
public sealed record LimitNode(double Limit = .6);
public sealed record ChannelOutputNode(string DeviceId = "", int Channel = 0);
public sealed record PumpOutputNode(string DeviceId = "");

public static class BuiltInNodes
{
    public const string Constant = "numeric.constant";
    public const string Waveform = "waveform.player";
    public const string Gain = "numeric.gain";
    public const string Limit = "numeric.limit";
    public const string ChannelOutput = "device.channel";
    public const string PumpOutput = "device.pump";

    public static NodeTypeRegistry CreateRegistry() => new NodeTypeRegistry()
        .Register(new NodeRegistration<ConstantNode>(Constant, "Pipeline.Node.Constant",
            [NumericPort.Output("value", "Pipeline.Port.Value")], c => double.IsFinite(c.Value), (c, _) => new ConstantProcessor(c.Value)))
        .Register(new NodeRegistration<WaveformNode>(Waveform, "Pipeline.Node.Waveform",
            [NumericPort.Output("level", "Pipeline.Port.Strength", "ratio"), NumericPort.Output("frequency", "Pipeline.Port.Frequency", "Hz"),
             NumericPort.Output("pulseWidth", "Pipeline.Port.PulseWidth", "ratio")], c => c.WaveformId != Guid.Empty && c.Channel is >= 0 and <= 2,
            (c, waves) => new WaveformProcessor(waves[c.WaveformId], c.Channel)))
        .Register(new NodeRegistration<GainNode>(Gain, "Pipeline.Node.Gain",
            [NumericPort.Input("value", "Pipeline.Port.Value", double.MinValue, double.MaxValue),
             NumericPort.Input("gain", "Pipeline.Port.Gain", -1000, 1000, 1), NumericPort.Output("value", "Pipeline.Port.Value")],
            c => double.IsFinite(c.Gain) && c.Gain is >= -1000 and <= 1000, (c, _) => new GainProcessor(c.Gain)))
        .Register(new NodeRegistration<LimitNode>(Limit, "Pipeline.Node.Limit",
            [NumericPort.Input("value", "Pipeline.Port.Value", double.MinValue, double.MaxValue),
             NumericPort.Input("limit", "Pipeline.Port.Limit", 0, 1, .6, "ratio"), NumericPort.Output("value", "Pipeline.Port.Value")],
            c => double.IsFinite(c.Limit) && c.Limit is >= 0 and <= 1, (c, _) => new LimitProcessor(c.Limit)))
        .Register(new NodeRegistration<ChannelOutputNode>(ChannelOutput, "Pipeline.Node.ChannelOutput",
            [NumericPort.Input("strength", "Pipeline.Port.Strength", 0, 1, 0, "ratio"),
             NumericPort.Input("frequency", "Pipeline.Port.Frequency", 1, 100, 20, "Hz"),
             NumericPort.Input("pulseWidth", "Pipeline.Port.PulseWidth", 0, 1, 1, "ratio"),
             NumericPort.Input("limit", "Pipeline.Port.Limit", 0, 1, .6, "ratio")],
            c => c.DeviceId is { Length: > 0 and <= 512 } && c.Channel is >= 0 and <= 2,
            (c, _) => new ChannelOutputProcessor(c), c => new(c.DeviceId, OutputBindingKind.Channel, c.Channel)))
        .Register(new NodeRegistration<PumpOutputNode>(PumpOutput, "Pipeline.Node.PumpOutput",
            [NumericPort.Input("direction", "Pipeline.Port.Direction", 0, 2, integer: true),
             NumericPort.Input("water", "Pipeline.Port.Water", 0, 1, integer: true),
             NumericPort.Input("peristalticSeconds", "Pipeline.Port.PeristalticSeconds", 1, 65535, 1, "s", true),
             NumericPort.Input("waterSeconds", "Pipeline.Port.WaterSeconds", 1, 65535, 1, "s", true)],
            c => c.DeviceId is { Length: > 0 and <= 512 }, (c, _) => new PumpOutputProcessor(c), c => new(c.DeviceId, OutputBindingKind.Pump)));
}
