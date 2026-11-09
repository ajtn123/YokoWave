using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class NativeOutputEditorViewModel : DeviceOutputEditorViewModel
{
    public bool StopWhenComplete { get => _settings.StopWhenComplete; set => Change(() => _settings = _settings with { StopWhenComplete = value }); }
    private NativeOutputNode _settings;
    private NativeProgramSettings _program;
    public NativeProgramEditorViewModel NativeEditor { get; }
    public NativeOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition,
        ((NativeOutputNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId)
    {
        _settings = (NativeOutputNode)editor.Registry.Get(Type).Read(definition.Configuration);
        _program = _settings.Settings ?? new();
        NativeEditor = new(_program, () => CanEdit);
        NativeEditor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(NativeEditor.Settings))
                return;
            if (NativeEditor.ValidationError is null)
                Change(() => _program = NativeEditor.Settings);
            Raise(nameof(ValidationError));
            Editor.Validate();
        };
        RefreshDevices();
    }
    public string ValidationError => NativeEditor.ValidationError is { } key ? Core.Localization.L10n.Get(key) : "";
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _settings with
    {
        DeviceId = DeviceId,
        Settings = _program
    }, Id);
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        NativeEditor.Refresh();
        Raise(nameof(ValidationError));
    }
    internal override void RefreshDevices()
    {
        base.RefreshDevices();
        NativeEditor.ConfigureDevice(Editor.FindDevice(DeviceId));
    }
}
