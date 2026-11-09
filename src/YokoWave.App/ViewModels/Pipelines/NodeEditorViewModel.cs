using System.Collections.ObjectModel;
using Avalonia;
using System.Text.Json;
using System.Windows.Input;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class NodePortViewModel(NodeEditorViewModel node, NodePort port) : ObservableObject
{
    private Point _anchor;
    private bool _connected;
    private bool _exposed;
    public bool IsExposed
    {
        get => _exposed; set
        {
            if (value != _exposed)
                Node.EditExposure(() => Set(ref _exposed, value));
        }
    }
    public NodeEditorViewModel Node { get; } = node;
    public NodePort Port { get; } = port;
    public Point Anchor { get => _anchor; set => Set(ref _anchor, value); }
    public bool IsConnected { get => _connected; set => Set(ref _connected, value); }
    public bool IsAvailable => Node.IsPortAvailable(Port);
    internal void RestoreExposure(bool value) => _exposed = value;
    public string Title => L10n.Get(Port.NameKey) + (Port.Parameter?.Unit is { Length: > 0 } unit ? $" ({(unit == "ratio" ? "0–1" : unit)})" : "");
    public string Detail => Port.Direction == PortDirection.Input && Port.Parameter is { } spec
        ? L10n.Format("Pipeline.Port.Range", spec.Minimum, spec.Maximum, spec.DefaultValue) : Title;
    internal void RefreshLocalization()
    {
        Raise(nameof(Title));
        Raise(nameof(Detail));
        Raise(nameof(IsAvailable));
    }
}

public sealed record ConnectionViewModel(NodePortViewModel Source, NodePortViewModel Target)
{
    public PipelineConnection Definition => new(new(Source.Node.Id, Source.Port.Id), new(Target.Node.Id, Target.Port.Id));
}

public abstract class NodeEditorViewModel : ObservableObject
{
    private Point _location;
    private bool _selected;
    private JsonElement? _parameterConfiguration;
    private bool _boost;
    private double _strengthLimit;
    public bool HasStrengthLimit => BuiltInNodes.IsOutput(Type);
    public double StrengthLimit
    {
        get => _strengthLimit;
        set
        {
            if (!HasStrengthLimit || !double.IsFinite(value) || value is < 0 or > 1 || value == _strengthLimit)
                return;
            Change(() => Set(ref _strengthLimit, value), live: true);
            Editor.Session.SetLimit(Id, value);
        }
    }
    public bool BoostHeld
    {
        get => _boost; set
        {
            if (Set(ref _boost, value))
                Editor.Session.Update(NodeControl.Create(Id, "boost", value));
        }
    }
    public bool HasAutomation => Type == BuiltInNodes.Automation;
    public bool HasCalibration => Type is BuiltInNodes.Deviation or BuiltInNodes.Counter;
    public bool HasTrigger => Type == BuiltInNodes.Command;
    public bool IsSubgraph => Type == SubgraphExpansion.Type;
    public IReadOnlyList<ParameterFieldViewModel> Parameters { get; }
    public bool HasParameters => Parameters.Count > 0;
    public ICommand ResetAutomationCommand { get; }
    protected PipelineEditorViewModel Editor { get; }
    public Guid Id { get; }
    public string Type { get; }
    public string Title => L10n.Get(Editor.Registry.Get(Type).NameKey);
    public virtual string Summary => "";
    public bool HasSummary => Summary.Length > 0;
    public virtual IReadOnlyList<SentStrengthViewModel> SentStrength => [];
    public virtual bool IsPortAvailable(NodePort port) => true;
    public virtual PresetItem? Preview => null;
    public double PreviewPhase => Preview is { } preset ?
        Editor.Session.NodeStatus.GetValueOrDefault(Id) is { } status ? 1 - status.RemainingSeconds / preset.Preset.PeriodSeconds :
        Editor.Session.Elapsed.TotalSeconds % preset.Preset.PeriodSeconds / preset.Preset.PeriodSeconds : 0;
    public ObservableCollection<NodePortViewModel> Inputs { get; } = [];
    public ObservableCollection<NodePortViewModel> Outputs { get; } = [];
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
    public string StatusText => Editor.Session.NodeStatus.TryGetValue(Id, out var status)
        ? L10n.Format("Pipeline.NodeStatus", L10n.Get("InputPhase." + status.Phase), status.Level, status.Count, Math.Ceiling(status.RemainingSeconds)) : "";
    public ICommand CalibrateCommand { get; }
    public ICommand TriggerCommand { get; }
    public ICommand ExpandCommand { get; }
    protected abstract NodeDefinition BuildDefinition();
    public NodeDefinition ToDefinition()
    {
        var definition = BuildDefinition();
        if (_parameterConfiguration is { } configuration)
            definition = definition with { Configuration = configuration };
        if (!HasStrengthLimit)
            return definition;
        var output = System.Text.Json.Nodes.JsonNode.Parse(definition.Configuration.GetRawText())!;
        output["Limit"] = StrengthLimit;
        return definition with { Configuration = JsonSerializer.SerializeToElement(output, PipelineJson.Options) };
    }
    public void ReportError(Exception error) => Editor.ReportError(error);
    protected NodeEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition)
    {
        Editor = editor;
        Id = definition.Id;
        Type = definition.Type;
        _strengthLimit = HasStrengthLimit ? (definition.Configuration.TryGetProperty("Limit", out var limit) ? limit.GetDouble() : Type == BuiltInNodes.PumpOutput ? 1 : .6) : 1;
        Parameters = NodeParameterForm.Create(definition, configuration => Change(() => _parameterConfiguration = configuration));
        CalibrateCommand = new ActionCommand(() => editor.Session.Update(NodeControl.Create(Id, "calibrate", true)));
        TriggerCommand = new ActionCommand(() => editor.Session.Update(NodeControl.Create(Id, "trigger", true)));
        ExpandCommand = new ActionCommand(editor.ExpandSubgraphs);
        ResetAutomationCommand = new ActionCommand(() => editor.Session.Update(NodeControl.Create(Id, "reset", true)));
        var registration = editor.Registry.Get(Type);
        foreach (var port in registration.PortsFor(registration.Read(definition.Configuration)))
            (port.Direction == PortDirection.Input ? Inputs : Outputs).Add(new(this, port));
    }
    protected void Change(Action change, bool live = false) => Editor.Edit(change, live: live);
    internal void EditExposure(Action change) => Editor.Edit(change);
    public virtual void RefreshLocalization()
    {
        Raise(nameof(Title));
        Raise(nameof(Summary));
        Raise(nameof(HasSummary));
        Raise(nameof(CanEdit));
        Raise(nameof(StatusText));
        Raise(nameof(Preview));
        Raise(nameof(PreviewPhase));
        if (Editor.Session.State != PipelineState.Running)
            BoostHeld = false;
        foreach (var port in Inputs.Concat(Outputs))
            port.RefreshLocalization();
        foreach (var parameter in Parameters)
            parameter.Refresh();
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
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new ConstantNode(Value), Id);
}

public interface IWaveformNodeEditor
{
    bool SelectWaveform(PresetItem preset);
}
public sealed class WaveformEditorNodeViewModel : NodeEditorViewModel, IWaveformNodeEditor
{
    private Guid _waveformId;
    public override string Summary => SelectedPreset?.Name ?? L10n.Get("Pipeline.Error.Waveform");
    public override PresetItem? Preview => SelectedPreset;
    public WaveformEditorNodeViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        var config = (WaveformNode)editor.Registry.Get(Type).Read(definition.Configuration);
        _waveformId = config.WaveformId;
    }
    public ObservableCollection<PresetItem> Presets => Editor.Presets;
    public PresetItem? SelectedPreset
    {
        get => Presets.FirstOrDefault(p => p.Preset.Id == _waveformId);
        set
        {
            if (value is not null && value.Preset.Id != _waveformId)
            {
                Change(() => { _waveformId = value.Preset.Id; Raise(); }, live: true);
                Editor.Session.Update(NodeControl.Create(Id, "waveform", value.Preset));
            }
        }
    }
    public override bool IsPortAvailable(NodePort port) => WaveformPorts.IsAvailable(port, SelectedPreset?.Preset);
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new WaveformNode(_waveformId), Id);
    public bool SelectWaveform(PresetItem preset)
    {
        SelectedPreset = preset;
        return true;
    }
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
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new GainNode(Gain), Id);
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
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new LimitNode(Limit), Id);
}

public abstract class DeviceOutputEditorViewModel : NodeEditorViewModel
{
    private string _deviceId;
    private readonly ObservableCollection<SentStrengthViewModel> _sentStrength = [];
    public override IReadOnlyList<SentStrengthViewModel> SentStrength => _sentStrength;
    protected virtual IEnumerable<int> MeterChannels => HasStrengthLimit && Editor.FindDevice(DeviceId) is { IsInput: false, Kind: not Core.DeviceKind.Pump } device
        ? Enumerable.Range(0, 3).Where(device.HasChannel) : [];
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
            {
                Change(() => { _deviceId = value.Id; Raise(); });
                RefreshDevices();
            }
        }
    }
    internal virtual void RefreshDevices() => Raise(nameof(SelectedDevice));
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        var channels = MeterChannels.ToArray();
        if (!_sentStrength.Select(meter => meter.Channel).SequenceEqual(channels))
        {
            _sentStrength.Clear();
            foreach (var channel in channels)
                _sentStrength.Add(new(channel));
        }
        var (frame, unknown) = Editor.ReadSentOutput(DeviceId);
        foreach (var meter in _sentStrength)
            meter.Update(meter.Channel == 0 ? frame.ChannelA : meter.Channel == 1 ? frame.ChannelB : frame.ChannelC, unknown);
    }
}

public sealed class ChannelOutputEditorViewModel : DeviceOutputEditorViewModel
{
    protected override IEnumerable<int> MeterChannels => [Channel];
    public ObservableCollection<SessionOption> Channels { get; } = [];
    public SessionOption? SelectedChannel { get => Channels.FirstOrDefault(c => c.Value == Channel); set { if (value is not null) Channel = value.Value; } }

    private int _channel;
    private ChannelOutputNode _settings;
    public ChannelOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition,
        ((ChannelOutputNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId)
    {
        _settings = (ChannelOutputNode)editor.Registry.Get(Type).Read(definition.Configuration);
        _channel = _settings.Channel;
        RefreshDevices();
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
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _settings with { DeviceId = DeviceId, Channel = Channel }, Id);
    internal override void RefreshDevices()
    {
        base.RefreshDevices();
        var channels = Editor.FindDevice(DeviceId) is { } device ? ChannelChoices.Create(device) : [];
        if (!Channels.Select(c => c.Value).SequenceEqual(channels.Select(c => c.Value)))
        {
            Channels.Clear();
            foreach (var channel in channels)
                Channels.Add(channel);
        }
        Raise(nameof(SelectedChannel));
    }
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var channel in Channels)
            channel.RefreshLocalization();
    }
}

public sealed class PumpOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : DeviceOutputEditorViewModel(editor, definition,
    ((PumpOutputNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId)
{
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new PumpOutputNode(DeviceId), Id);
}

public sealed record PipelineDeviceOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public class ConfigurationNodeViewModel : NodeEditorViewModel
{
    protected NodeDefinition Definition { get; set; }
    public ConfigurationNodeViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition) => Definition = definition;
    protected override NodeDefinition BuildDefinition() => Definition;
}

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
