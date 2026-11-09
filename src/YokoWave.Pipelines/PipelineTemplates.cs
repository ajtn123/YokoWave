using YokoWave.Core;

namespace YokoWave.Pipelines;

public sealed record PipelineTemplate(string Key, string NameKey, Func<TemplateParameters, PipelineDocument> Create);
public sealed record TemplateParameters(string Name, string DeviceId = "$device", string SensorId = "$sensor",
    WaveformAsset? Waveform = null, double Limit = .6, int Channels = 2);

/// <summary>Templates produce ordinary graphs. No template identifier participates in runtime dispatch.</summary>
public static class PipelineTemplates
{
    public static IReadOnlyList<PipelineTemplate> All { get; } =
    [
        new("waveform", "Template.Waveform", Waveform),
        new("touch", "Template.Touch", Touch),
        new("audio", "Template.Audio", Audio),
        new("restraint", "Template.Restraint", Restraint),
        new("exercise", "Template.Exercise", Exercise),
        new("sleep", "Template.SleepDeprivation", Sleep),
        new("sleep.random", "Pipeline.Template.RandomSleep", p => SleepVariant(p, true, false)),
        new("sleep.activity", "Pipeline.Template.ActivitySleep", p => SleepVariant(p, false, true)),
        new("pump", "Template.Pump", Pump),
        new("native", "Template.NativeProgram", Native)
    ];

    public static PipelineDocument Create(string key, TemplateParameters parameters) => All.Single(t => t.Key == key).Create(parameters);
    private static WaveformAsset Asset(TemplateParameters parameters) => parameters.Waveform ?? WaveformAsset.BuiltIn[0];

    private static PipelineDocument Waveform(TemplateParameters p)
    {
        var b = new PipelineBuilder(p.Name, p.Limit);
        for (int channel = 0; channel < p.Channels; channel++)
        {
            var wave = b.Add(BuiltInNodes.Playlist, new PlaylistNode(Asset(p).Id, [Asset(p).Id], Phase: channel == 1 ? .5 : 0));
            b.Channel(p.DeviceId, channel, wave, WaveformPorts.Level(channel < Asset(p).ChannelCount ? channel : 0), wave, "frequency", wave, "pulseWidth");
        }
        return b.Build();
    }
    private static PipelineDocument Touch(TemplateParameters p)
    {
        var b = new PipelineBuilder(p.Name, p.Limit);
        var input = b.Add(BuiltInNodes.Touch, new TouchNode());
        for (int channel = 0; channel < p.Channels; channel++)
            b.Channel(p.DeviceId, channel, input, "level", input, "frequency", input, "pulseWidth");
        return b.Build();
    }
    private static PipelineDocument Audio(TemplateParameters p)
    {
        var b = new PipelineBuilder(p.Name, p.Limit);
        var input = b.Add(BuiltInNodes.Audio, new AudioNode(new()));
        for (int channel = 0; channel < p.Channels; channel++)
        {
            var map = b.Add(BuiltInNodes.ParameterMap, new ParameterMapNode());
            b.Connect(input, channel == 1 ? "right" : "left", map, "value");
            b.Channel(p.DeviceId, channel, map, "level", map, "frequency", map, "pulseWidth");
        }
        return b.Build();
    }
    private static PipelineDocument Restraint(TemplateParameters p)
    {
        var b = new PipelineBuilder(p.Name, p.Limit);
        var sensor = b.Add(BuiltInNodes.Sensor, new SensorNode(p.SensorId));
        var deviation = b.Add(BuiltInNodes.Deviation, new DeviationNode());
        var range = b.Add(BuiltInNodes.RangeMap, new RangeMapNode());
        var map = b.Add(BuiltInNodes.ParameterMap, new ParameterMapNode());
        b.Connect(sensor, "angle", deviation, "value");
        b.Connect(deviation, "value", range, "value");
        b.Connect(range, "value", map, "value");
        for (int channel = 0; channel < p.Channels; channel++)
            b.Channel(p.DeviceId, channel, map, "level", map, "frequency", map, "pulseWidth");
        return b.Build();
    }
    private static PipelineDocument Exercise(TemplateParameters p)
    {
        var b = new PipelineBuilder(p.Name, p.Limit);
        var sensor = b.Add(BuiltInNodes.Sensor, new SensorNode(p.SensorId));
        var count = b.Add(BuiltInNodes.Counter, new CounterNode(50.5 - .503 * 80));
        var cycle = b.Add(BuiltInNodes.Cycle, new CycleNode(10, 5, ActivityReset: true, RequireActivity: true));
        b.Connect(sensor, "angle", count, "angle");
        b.Connect(sensor, "steps", count, "steps");
        b.Connect(count, "activity", cycle, "activity");
        for (int channel = 0; channel < p.Channels; channel++)
        {
            var wave = b.Add(BuiltInNodes.Playlist, new PlaylistNode(Asset(p).Id, [Asset(p).Id], Phase: channel == 1 ? .5 : 0));
            var gate = b.Add(BuiltInNodes.Gain, new GainNode());
            b.Connect(wave, WaveformPorts.Level(channel < Asset(p).ChannelCount ? channel : 0), gate, "value");
            b.Connect(cycle, "gate", gate, "gain");
            b.Channel(p.DeviceId, channel, gate, "value", wave, "frequency", wave, "pulseWidth");
        }
        return b.Build();
    }
    private static PipelineDocument Sleep(TemplateParameters p) => SleepVariant(p, false, false);
    private static PipelineDocument SleepVariant(TemplateParameters p, bool random, bool activity)
    {
        var b = new PipelineBuilder(p.Name, p.Limit);
        var cycle = b.Add(BuiltInNodes.Cycle, new CycleNode(Random: random, ActivityReset: activity, RequireActivity: activity));
        if (activity)
        {
            var sensor = b.Add(BuiltInNodes.Sensor, new SensorNode(p.SensorId));
            var count = b.Add(BuiltInNodes.Counter, new CounterNode(1));
            b.Connect(sensor, "angle", count, "angle");
            b.Connect(sensor, "steps", count, "steps");
            b.Connect(count, "activity", cycle, "activity");
        }
        for (int channel = 0; channel < p.Channels; channel++)
        {
            var wave = b.Add(BuiltInNodes.Playlist, new PlaylistNode(Asset(p).Id, random ? WaveformAsset.BuiltIn.Select(w => w.Id).ToArray() : [Asset(p).Id],
                random ? PlaybackOrder.Random : PlaybackOrder.Repeat, Phase: channel == 1 && !random ? .5 : 0, RestartOnTrigger: random));
            var gate = b.Add(BuiltInNodes.Gain, new GainNode());
            b.Connect(wave, WaveformPorts.Level(channel < Asset(p).ChannelCount ? channel : 0), gate, "value");
            b.Connect(cycle, "gate", gate, "gain");
            b.Connect(cycle, "trigger", wave, "trigger");
            b.Channel(p.DeviceId, channel, gate, "value", wave, "frequency", wave, "pulseWidth");
        }
        return b.Build();
    }
    private static PipelineDocument Pump(TemplateParameters p)
    {
        var b = new PipelineBuilder(p.Name, p.Limit);
        var timer = b.Add(BuiltInNodes.PumpTimer, new PumpTimerNode(new()));
        var output = b.Add(BuiltInNodes.PumpOutput, new PumpOutputNode(p.DeviceId, p.Limit));
        foreach (string? port in new[] { "direction", "water", "peristalticSeconds", "waterSeconds" })
            b.Connect(timer, port, output, port);
        return b.Build();
    }
    private static PipelineDocument Native(TemplateParameters p)
    {
        var b = new PipelineBuilder(p.Name, p.Limit);
        var output = b.Add(BuiltInNodes.NativeOutput, new NativeOutputNode(p.DeviceId, Limit: p.Limit));
        for (int channel = 0; channel < p.Channels; channel++)
        {
            var strength = b.Add(BuiltInNodes.Constant, new ConstantNode());
            b.Connect(strength, "value", output, "strength" + (char)('A' + channel));
        }
        return b.Build();
    }
}

public sealed class PipelineBuilder(string name, double defaultOutputLimit = .6)
{
    private readonly List<NodeDefinition> _nodes = [];
    private readonly List<PipelineConnection> _connections = [];
    public NodeDefinition Add<T>(string type, T configuration)
    {
        var node = NodeDefinition.Create(type, configuration);
        _nodes.Add(node);
        return node;
    }
    public void Connect(NodeDefinition source, string output, NodeDefinition target, string input) =>
        _connections.Add(new(new(source.Id, output), new(target.Id, input)));

    public NodeDefinition Channel(string device, int channel, NodeDefinition level, string levelPort,
        NodeDefinition frequency, string frequencyPort, NodeDefinition width, string widthPort)
    {
        var strength = Add(BuiltInNodes.Constant, new ConstantNode());
        var automation = Add(BuiltInNodes.Automation, new AutomationNode(new()));
        var gain = Add(BuiltInNodes.Gain, new GainNode());
        var output = Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(device, channel, Limit: defaultOutputLimit));
        Connect(strength, "value", automation, "value");
        Connect(automation, "value", gain, "gain");
        Connect(level, levelPort, gain, "value");
        Connect(gain, "value", output, "strength");
        Connect(frequency, frequencyPort, output, "frequency");
        Connect(width, widthPort, output, "pulseWidth");
        return output;
    }

    public PipelineDocument Build() => new(Guid.NewGuid(), name, _nodes.ToArray(), _connections.ToArray(),
        _nodes.Select((n, i) => new NodeLayout(n.Id, i % 5 * 260 + 40, i / 5 * 200 + 40)).ToArray());
}
