using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class GainEditorViewModel : NodeEditorViewModel
{
    private double _gain;
    public double Gain
    {
        get => _gain;
        set
        {
            if (double.IsFinite(value) && value is >= -1000 and <= 1000 && value != _gain)
                Change(() => Set(ref _gain, value));
        }
    }
    public GainEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _gain = ((GainNode)editor.Registry.Get(Type).Read(definition.Configuration)).Gain;
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new GainNode(Gain), Id);
}
