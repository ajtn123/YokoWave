using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class ConstantEditorViewModel : NodeEditorViewModel
{
    private double _value;
    public override string Summary => Value.ToString("G4", L10n.Current.Culture);
    public double Value
    {
        get => _value;
        set
        {
            if (!double.IsFinite(value) || value == _value)
                return;
            Change(() => Set(ref _value, value), live: true);
            Editor.Session.SetConstant(Id, _value);
        }
    }
    public ConstantEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _value = ((ConstantNode)editor.Registry.Get(Type).Read(definition.Configuration)).Value;
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new ConstantNode(Value), Id);
}
