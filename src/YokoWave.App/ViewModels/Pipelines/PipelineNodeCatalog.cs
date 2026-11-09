using YokoWave.Pipelines;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels.Pipelines;

/// <summary>Editor factories are UI concerns; the graph registry and runtime have no UI dependency.</summary>
public sealed class PipelineNodeCatalog
{
    private readonly Dictionary<string, Func<PipelineEditorViewModel, NodeDefinition, NodeEditorViewModel>> _editors = new(StringComparer.Ordinal)
    {
        [BuiltInNodes.Constant] = (e, d) => new ConstantEditorViewModel(e, d),
        [BuiltInNodes.Waveform] = (e, d) => new WaveformEditorNodeViewModel(e, d),
        [BuiltInNodes.Gain] = (e, d) => new GainEditorViewModel(e, d),
        [BuiltInNodes.Limit] = (e, d) => new LimitEditorViewModel(e, d),
        [BuiltInNodes.ChannelOutput] = (e, d) => new ChannelOutputEditorViewModel(e, d),
        [BuiltInNodes.PumpOutput] = (e, d) => new PumpOutputEditorViewModel(e, d)
        ,
        [BuiltInNodes.Playlist] = (e, d) => new PlaylistEditorViewModel(e, d)
        ,
        [BuiltInNodes.Audio] = (e, d) => new AudioNodeEditorViewModel(e, d)
        ,
        [BuiltInNodes.Touch] = (e, d) => new TouchNodeEditorViewModel(e, d)
        ,
        [BuiltInNodes.Sensor] = (e, d) => new ResourceNodeViewModel(e, d)
        ,
        [BuiltInNodes.NativeOutput] = (e, d) => new NativeOutputEditorViewModel(e, d)
        ,
        [BuiltInNodes.Command] = (e, d) => new CommandNodeEditorViewModel(e, d)
        ,
        [BuiltInNodes.RouterOutput] = (e, d) => new ResourceNodeViewModel(e, d)
    };
    private readonly Dictionary<string, Func<PipelineEditorViewModel, NodeDefinition>> _defaults = new(StringComparer.Ordinal)
    {
        [BuiltInNodes.Constant] = _ => NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode()),
        [BuiltInNodes.Waveform] = e => NodeDefinition.Create(BuiltInNodes.Waveform, new WaveformNode(e.Presets.First().Preset.Id)),
        [BuiltInNodes.Gain] = _ => NodeDefinition.Create(BuiltInNodes.Gain, new GainNode()),
        [BuiltInNodes.Limit] = _ => NodeDefinition.Create(BuiltInNodes.Limit, new LimitNode()),
        [BuiltInNodes.ChannelOutput] = _ => NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode()),
        [BuiltInNodes.PumpOutput] = _ => NodeDefinition.Create(BuiltInNodes.PumpOutput, new PumpOutputNode())
        ,
        [BuiltInNodes.Playlist] = e => NodeDefinition.Create(BuiltInNodes.Playlist, new PlaylistNode(e.Presets.First().Preset.Id, e.Presets.Select(p => p.Preset.Id).ToArray()))
        ,
        [BuiltInNodes.Touch] = _ => NodeDefinition.Create(BuiltInNodes.Touch, new TouchNode())
        ,
        [BuiltInNodes.Audio] = _ => NodeDefinition.Create(BuiltInNodes.Audio, new AudioNode(new()))
        ,
        [BuiltInNodes.Sensor] = _ => NodeDefinition.Create(BuiltInNodes.Sensor, new SensorNode())
        ,
        [BuiltInNodes.Deviation] = _ => NodeDefinition.Create(BuiltInNodes.Deviation, new DeviationNode())
        ,
        [BuiltInNodes.RangeMap] = _ => NodeDefinition.Create(BuiltInNodes.RangeMap, new RangeMapNode())
        ,
        [BuiltInNodes.Counter] = _ => NodeDefinition.Create(BuiltInNodes.Counter, new CounterNode())
        ,
        [BuiltInNodes.Cycle] = _ => NodeDefinition.Create(BuiltInNodes.Cycle, new CycleNode())
        ,
        [BuiltInNodes.ParameterMap] = _ => NodeDefinition.Create(BuiltInNodes.ParameterMap, new ParameterMapNode())
        ,
        [BuiltInNodes.Automation] = _ => NodeDefinition.Create(BuiltInNodes.Automation, new AutomationNode(new()))
        ,
        [BuiltInNodes.PumpTimer] = _ => NodeDefinition.Create(BuiltInNodes.PumpTimer, new PumpTimerNode(new()))
        ,
        [BuiltInNodes.NativeOutput] = _ => NodeDefinition.Create(BuiltInNodes.NativeOutput, new NativeOutputNode())
        ,
        [BuiltInNodes.Command] = _ => NodeDefinition.Create(BuiltInNodes.Command, new CommandNode("", new QueryDevice(DeviceQuery.All)))
        ,
        [BuiltInNodes.FrameInput] = _ => NodeDefinition.Create(BuiltInNodes.FrameInput, new FrameInputNode())
        ,
        [BuiltInNodes.RouterOutput] = _ => NodeDefinition.Create(BuiltInNodes.RouterOutput, new RouterOutputNode())
        ,
        [SubgraphExpansion.Type] = _ => NodeDefinition.Create(SubgraphExpansion.Type, new SubgraphNode(
            new PipelineBuilder(L10n.Get("Pipeline.Node.Subgraph")).Build(), [], []))
    };
    public PipelineNodeCatalog()
    {
        Register(BuiltInNodes.Mix, _ => NodeDefinition.Create(BuiltInNodes.Mix, new MixNode()));
        Register(BuiltInNodes.Threshold, _ => NodeDefinition.Create(BuiltInNodes.Threshold, new ThresholdNode()));
        Register(BuiltInNodes.Smooth, _ => NodeDefinition.Create(BuiltInNodes.Smooth, new SmoothNode()));
        Register(BuiltInNodes.Select, _ => NodeDefinition.Create(BuiltInNodes.Select, new SelectNode()));
        Register(BuiltInNodes.SimulatedInput, _ => NodeDefinition.Create(BuiltInNodes.SimulatedInput, new SimulatedInputNode()), (e, d) => new SimulatedInputEditorViewModel(e, d));
        _editors[BuiltInNodes.PumpTimer] = (e, d) => new PumpTimerEditorViewModel(e, d);
        Register(RemotePipelineNodes.GattServer, _ => NodeDefinition.Create(RemotePipelineNodes.GattServer, new GattServerNode(["$device"], new())), (e, d) => new ServerNodeEditorViewModel(e, d));
        Register(RemotePipelineNodes.ControlServer, _ => NodeDefinition.Create(RemotePipelineNodes.ControlServer, new ControlServerNode("$device", new())), (e, d) => new ServerNodeEditorViewModel(e, d));
    }
    public NodeEditorViewModel Create(PipelineEditorViewModel editor, NodeDefinition definition) => _editors.TryGetValue(definition.Type, out var factory)
        ? factory(editor, definition) : new ConfigurationNodeViewModel(editor, definition);
    public NodeDefinition Default(PipelineEditorViewModel editor, string type) => _defaults[type](editor);
    public void Register(string type, Func<PipelineEditorViewModel, NodeDefinition> create, Func<PipelineEditorViewModel, NodeDefinition, NodeEditorViewModel>? editorFactory = null)
    {
        _defaults.Add(type, create);
        if (editorFactory is not null)
            _editors.Add(type, editorFactory);
    }
}
