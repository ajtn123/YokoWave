using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class SimulatedInputEditorViewModel : NodeEditorViewModel
{
    private double _value;
    public SimulatedInputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _value = ((SimulatedInputNode)editor.Registry.Get(Type).Read(definition.Configuration)).Value;
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
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new SimulatedInputNode(Value), Id);
}

public sealed class PumpTimerEditorViewModel : NodeEditorViewModel
{
    public bool StopWhenComplete { get => _settings.StopWhenComplete; set => Change(() => _settings = _settings with { StopWhenComplete = value }); }
    private PumpTimerNode _settings;
    public IReadOnlyList<SessionOption> Directions { get; } = Enum.GetValues<PumpDirection>().Select(d => new SessionOption((int)d, "PumpDirection." + d)).ToArray();
    public SessionOption Direction
    {
        get => Directions.Single(d => d.Value == (int)_settings.Settings.Direction); set
        {
            if (value is not null)
                Update(_settings.Settings with
                {
                    Direction = (PumpDirection)value.Value
                });
        }
    }
    public bool Water
    {
        get => _settings.Settings.Water; set => Update(_settings.Settings with { Water = value });
    }
    public int PeristalticSeconds
    {
        get => _settings.Settings.PeristalticSeconds; set => Update(_settings.Settings with { PeristalticSeconds = Math.Clamp(value, 0, 65535) });
    }
    public int WaterSeconds
    {
        get => _settings.Settings.WaterSeconds; set => Update(_settings.Settings with { WaterSeconds = Math.Clamp(value, 0, 65535) });
    }
    public PumpTimerEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _settings = (PumpTimerNode)editor.Registry.Get(Type).Read(definition.Configuration);
    private void Update(PumpOutput pump)
    {
        if (pump == _settings.Settings)
            return;
        Change(() => _settings = _settings with { Settings = pump }, live: true);
        Editor.Session.Update(NodeControl.Create(Id, "pump", pump));
        foreach (var property in new[] { nameof(Direction), nameof(Water), nameof(PeristalticSeconds), nameof(WaterSeconds) })
            Raise(property);
    }
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _settings, Id);
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var direction in Directions)
            direction.RefreshLocalization();
    }
}
