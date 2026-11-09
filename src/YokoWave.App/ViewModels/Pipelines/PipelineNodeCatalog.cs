using YokoWave.Pipelines;

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
    };
    private readonly Dictionary<string, Func<PipelineEditorViewModel, NodeDefinition>> _defaults = new(StringComparer.Ordinal)
    {
        [BuiltInNodes.Constant] = _ => NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode()),
        [BuiltInNodes.Waveform] = e => NodeDefinition.Create(BuiltInNodes.Waveform, new WaveformNode(e.Presets.First().Preset.Id)),
        [BuiltInNodes.Gain] = _ => NodeDefinition.Create(BuiltInNodes.Gain, new GainNode()),
        [BuiltInNodes.Limit] = _ => NodeDefinition.Create(BuiltInNodes.Limit, new LimitNode()),
        [BuiltInNodes.ChannelOutput] = _ => NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode()),
        [BuiltInNodes.PumpOutput] = _ => NodeDefinition.Create(BuiltInNodes.PumpOutput, new PumpOutputNode())
    };
    public NodeEditorViewModel Create(PipelineEditorViewModel editor, NodeDefinition definition) => _editors[definition.Type](editor, definition);
    public NodeDefinition Default(PipelineEditorViewModel editor, string type) => _defaults[type](editor);
}
