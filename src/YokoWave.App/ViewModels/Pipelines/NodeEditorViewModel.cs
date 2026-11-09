using System.Collections.ObjectModel;
using Avalonia;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class NumericPortViewModel(NodeEditorViewModel node, NumericPort port) : ObservableObject
{
    private Point _anchor;
    private bool _connected;
    public NodeEditorViewModel Node { get; } = node;
    public NumericPort Port { get; } = port;
    public Point Anchor { get => _anchor; set => Set(ref _anchor, value); }
    public bool IsConnected { get => _connected; set => Set(ref _connected, value); }
    public string Title => L10n.Get(Port.NameKey) + (Port.Parameter?.Unit is { Length: > 0 } unit ? $" ({(unit == "ratio" ? "0–1" : unit)})" : "");
    public string Detail => Port.Direction == PortDirection.Input && Port.Parameter is { } spec
        ? L10n.Format("Pipeline.Port.Range", spec.Minimum, spec.Maximum, spec.DefaultValue) : Title;
    internal void RefreshLocalization() { Raise(nameof(Title)); Raise(nameof(Detail)); }
}

public sealed record ConnectionViewModel(NumericPortViewModel Source, NumericPortViewModel Target)
{
    public PipelineConnection Definition => new(new(Source.Node.Id, Source.Port.Id), new(Target.Node.Id, Target.Port.Id));
}

public abstract class NodeEditorViewModel : ObservableObject
{
    private Point _location;
    private bool _selected;
    protected PipelineEditorViewModel Editor { get; }
    public Guid Id { get; }
    public string Type { get; }
    public string Title => L10n.Get(Editor.Registry.Get(Type).NameKey);
    public virtual string Summary => "";
    public ObservableCollection<NumericPortViewModel> Inputs { get; } = [];
    public ObservableCollection<NumericPortViewModel> Outputs { get; } = [];
    public Point Location
    {
        get => _location;
        set
        {
            if (value != _location && double.IsFinite(value.X) && double.IsFinite(value.Y))
                Editor.Edit(() => Set(ref _location, value), layoutOnly: true);
        }
    }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool CanEdit => Editor.CanEdit;
    public abstract NodeDefinition ToDefinition();
    protected NodeEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition)
    {
        Editor = editor;
        Id = definition.Id;
        Type = definition.Type;
        foreach (var port in editor.Registry.Get(Type).Ports)
            (port.Direction == PortDirection.Input ? Inputs : Outputs).Add(new(this, port));
    }
    protected void Change(Action change, bool live = false) => Editor.Edit(change, live: live);
    public virtual void RefreshLocalization()
    {
        Raise(nameof(Title));
        Raise(nameof(Summary));
        Raise(nameof(CanEdit));
        foreach (var port in Inputs.Concat(Outputs))
            port.RefreshLocalization();
    }
    internal void RestoreLocation(Point point) => _location = point;
}

public sealed class ConstantEditorViewModel : NodeEditorViewModel
{
    private double _value;
    public override string Summary => Value.ToString("G4", L10n.Current.Culture);
    public ConstantEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _value = ((ConstantNode)editor.Registry.Get(Type).Read(definition.Configuration)).Value;
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
    public override NodeDefinition ToDefinition() => NodeDefinition.Create(Type, new ConstantNode(Value), Id);
}

public sealed class WaveformEditorNodeViewModel : NodeEditorViewModel
{
    private Guid _waveformId;
    private int _channel;
    public override string Summary => SelectedPreset?.Name ?? L10n.Get("Pipeline.Error.Waveform");
    public WaveformEditorNodeViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        var config = (WaveformNode)editor.Registry.Get(Type).Read(definition.Configuration);
        _waveformId = config.WaveformId;
        _channel = config.Channel;
    }
    public ObservableCollection<PresetItem> Presets => Editor.Presets;
    public PresetItem? SelectedPreset
    {
        get => Presets.FirstOrDefault(p => p.Preset.Id == _waveformId);
        set
        {
            if (value is not null && value.Preset.Id != _waveformId)
                Change(() => { _waveformId = value.Preset.Id; Raise(); });
        }
    }
    public int Channel
    {
        get => _channel;
        set
        {
            if (value is >= 0 and <= 2 && value != _channel)
                Change(() => Set(ref _channel, value));
        }
    }
    public override NodeDefinition ToDefinition() => NodeDefinition.Create(Type, new WaveformNode(_waveformId, Channel), Id);
}

public sealed class GainEditorViewModel : NodeEditorViewModel
{
    private double _gain;
    public GainEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _gain = ((GainNode)editor.Registry.Get(Type).Read(definition.Configuration)).Gain;
    public double Gain
    {
        get => _gain;
        set
        {
            if (double.IsFinite(value) && value is >= -1000 and <= 1000 && value != _gain)
                Change(() => Set(ref _gain, value));
        }
    }
    public override NodeDefinition ToDefinition() => NodeDefinition.Create(Type, new GainNode(Gain), Id);
}

public sealed class LimitEditorViewModel : NodeEditorViewModel
{
    private double _limit;
    public LimitEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) =>
        _limit = ((LimitNode)editor.Registry.Get(Type).Read(definition.Configuration)).Limit;
    public double Limit
    {
        get => _limit;
        set
        {
            if (double.IsFinite(value) && value is >= 0 and <= 1 && value != _limit)
                Change(() => Set(ref _limit, value));
        }
    }
    public override NodeDefinition ToDefinition() => NodeDefinition.Create(Type, new LimitNode(Limit), Id);
}

public abstract class DeviceOutputEditorViewModel : NodeEditorViewModel
{
    private string _deviceId;
    public override string Summary => SelectedDevice?.Name ?? DeviceId;
    protected DeviceOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition, string deviceId) : base(editor, definition) => _deviceId = deviceId;
    public string DeviceId => _deviceId;
    public ObservableCollection<PipelineDeviceOption> Devices => Editor.Devices;
    public PipelineDeviceOption? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Id == _deviceId);
        set
        {
            if (value is not null && value.Id != _deviceId)
                Change(() => { _deviceId = value.Id; Raise(); });
        }
    }
    internal void RefreshDevices() => Raise(nameof(SelectedDevice));
}

public sealed class ChannelOutputEditorViewModel : DeviceOutputEditorViewModel
{
    private int _channel;
    public ChannelOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition,
        ((ChannelOutputNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId) =>
        _channel = ((ChannelOutputNode)editor.Registry.Get(Type).Read(definition.Configuration)).Channel;
    public int Channel
    {
        get => _channel;
        set
        {
            if (value is >= 0 and <= 2 && value != _channel)
                Change(() => Set(ref _channel, value));
        }
    }
    public override NodeDefinition ToDefinition() => NodeDefinition.Create(Type, new ChannelOutputNode(DeviceId, Channel), Id);
}

public sealed class PumpOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : DeviceOutputEditorViewModel(editor, definition,
    ((PumpOutputNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId)
{
    public override NodeDefinition ToDefinition() => NodeDefinition.Create(Type, new PumpOutputNode(DeviceId), Id);
}

public sealed record PipelineDeviceOption(string Id, string Name)
{
    public override string ToString() => Name;
}
