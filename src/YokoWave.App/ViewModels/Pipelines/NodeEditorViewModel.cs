using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

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
        get => _boost;
        set
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
    public virtual WaveformItem? Preview => null;
    public double PreviewPhase => Preview is { } waveform ?
        Editor.Session.NodeStatus.GetValueOrDefault(Id) is { } status ? 1 - status.RemainingSeconds / waveform.Asset.PeriodSeconds :
        Editor.Session.Elapsed.TotalSeconds % waveform.Asset.PeriodSeconds / waveform.Asset.PeriodSeconds : 0;
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
    public virtual bool IsPortAvailable(NodePort port) => true;
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
