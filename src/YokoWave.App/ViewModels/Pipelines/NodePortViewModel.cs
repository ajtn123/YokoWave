using Avalonia;
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
        get => _exposed;
        set
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
    public string Title => L10n.Get(Port.NameKey) + (Port.Parameter?.Unit is { Length: > 0 } unit ? $" ({(unit == "ratio" ? "0–1" : unit)})" : "");
    public string Detail => Port.Direction == PortDirection.Input && Port.Parameter is { } spec
        ? L10n.Format("Pipeline.Port.Range", spec.Minimum, spec.Maximum, spec.DefaultValue) : Title;
    internal void RestoreExposure(bool value) => _exposed = value;
    internal void RefreshLocalization()
    {
        Raise(nameof(Title));
        Raise(nameof(Detail));
        Raise(nameof(IsAvailable));
    }
}
