using System.Text.Json;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class ResourceNodeViewModel : DeviceOutputEditorViewModel
{
    private readonly NodeDefinition _definition;
    public ResourceNodeViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition,
        definition.Configuration.GetProperty("DeviceId").GetString() ?? "") => _definition = definition;
    protected override NodeDefinition BuildDefinition()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(_definition.Configuration.GetRawText())!.AsObject();
        json["DeviceId"] = DeviceId;
        return _definition with
        {
            Configuration = JsonSerializer.SerializeToElement(json, PipelineJson.Options)
        };
    }
}
