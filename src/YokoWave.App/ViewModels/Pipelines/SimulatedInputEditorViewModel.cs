using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class SimulatedInputEditorViewModel : NodeEditorViewModel
{
    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            if (!double.IsFinite(value) || value == _value)
                return;
            Change(() => Set(ref _value, value), live: true);
            Editor.Session.Update(NodeControl.Create(Id, "value", value));
        }
    }
    public SimulatedInputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _value = ((SimulatedInputNode)editor.Registry.Get(Type).Read(definition.Configuration)).Value;
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new SimulatedInputNode(Value), Id);
}
