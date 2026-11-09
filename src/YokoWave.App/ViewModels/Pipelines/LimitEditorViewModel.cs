using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class LimitEditorViewModel : NodeEditorViewModel
{
    private double _limit;
    public double Limit
    {
        get => _limit;
        set
        {
            if (double.IsFinite(value) && value is >= 0 and <= 1 && value != _limit)
                Change(() => Set(ref _limit, value));
        }
    }
    public LimitEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _limit = ((LimitNode)editor.Registry.Get(Type).Read(definition.Configuration)).Limit;
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new LimitNode(Limit), Id);
}
