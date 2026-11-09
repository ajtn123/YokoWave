using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public class ConfigurationNodeViewModel : NodeEditorViewModel
{
    protected NodeDefinition Definition { get; set; }
    public ConfigurationNodeViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) => Definition = definition;
    protected override NodeDefinition BuildDefinition() => Definition;
}
