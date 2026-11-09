using System.Text.Json;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class CommandNodeEditorViewModel : DeviceOutputEditorViewModel
{
    private CommandNode _settings;
    private ProtocolCommandOption? _selected;
    private string _commandJson;
    public IReadOnlyList<ProtocolCommandOption> Commands => ProtocolFeaturesViewModel.AllCommands.Where(c =>
        Editor.FindDevice(DeviceId)?.Profile is not { } profile || profile.Supports(c.Capability)).ToArray();
    public IReadOnlyList<ParameterFieldViewModel> CommandParameters { get; private set; } = [];
    public ProtocolCommandOption? SelectedCommand
    {
        get => _selected;
        set
        {
            if (!CanEdit || value is null || value == _selected)
                return;
            Change(() =>
            {
                _selected = value;
                _commandJson = value.Template;
                _settings = _settings with { Command = value.Parse(_commandJson) };
                Raise();
                RefreshParameters();
            });
        }
    }
    public bool OnStart
    {
        get => _settings.OnStart; set => Change(() => { _settings = _settings with { OnStart = value }; Raise(); });
    }
    public CommandNodeEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition,
        ((CommandNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId)
    {
        _settings = (CommandNode)editor.Registry.Get(Type).Read(definition.Configuration);
        _selected = Commands.FirstOrDefault(c => SameKind(c.Parse(c.Template), _settings.Command));
        _commandJson = JsonSerializer.Serialize(_settings.Command, _settings.Command.GetType(), PipelineJson.Options);
        RefreshParameters();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SelectedDevice))
                return;
            Raise(nameof(Commands));
            RefreshParameters();
        };
    }
    private static bool SameKind(DeviceCommand a, DeviceCommand b) => a.GetType() == b.GetType() &&
        (a is not ConfigurePaw pa || b is ConfigurePaw pb && pa.Trigger.GetType() == pb.Trigger.GetType());
    private void RefreshParameters()
    {
        CommandParameters = ProtocolParameterForm.Create(_commandJson, _selected?.Key ?? "", Editor.FindDevice(DeviceId)?.Profile, json =>
        {
            if (_selected is { } command)
                Change(() =>
                {
                    _commandJson = json;
                    _settings = _settings with { Command = command.Parse(json) };
                });
        });
        Raise(nameof(CommandParameters));
    }
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _settings with { DeviceId = DeviceId }, Id);
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var command in Commands)
            command.Refresh();
        foreach (var field in CommandParameters)
            field.Refresh();
    }
}
