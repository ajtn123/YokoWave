using YokoWave.Pipelines;
using YokoWave.Remote.DgLab.V4;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class DgLabAppEditorViewModel : DeviceOutputEditorViewModel
{
    private string _url;
    private string _controllerId;
    public override IReadOnlyList<PipelineDeviceOption> Devices => base.Devices.Where(option => option.Id.StartsWith('$') ||
        Editor.FindDevice(option.Id) is { } device && Editor.Registry.Get(Type).AcceptsDevice(new DgLabV4AppNode(DeviceId), device)).ToArray();
    public string Url
    {
        get => _url;
        set { if (value != _url) Change(() => Set(ref _url, value, nameof(Url))); }
    }
    public string ControllerId
    {
        get => _controllerId;
        set { if (value != _controllerId) Change(() => Set(ref _controllerId, value, nameof(ControllerId))); }
    }

    public DgLabAppEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition,
        definition.Configuration.GetProperty("DeviceId").GetString() ?? "")
    {
        var configuration = (DgLabV4AppNode)editor.Registry.Get(definition.Type).Read(definition.Configuration);
        _url = configuration.Url;
        _controllerId = configuration.ControllerId;
    }

    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new DgLabV4AppNode(DeviceId, Url, ControllerId), Id);
}
