using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PumpOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : DeviceOutputEditorViewModel(editor, definition,
    ((PumpOutputNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId)
{
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new PumpOutputNode(DeviceId), Id);
}
