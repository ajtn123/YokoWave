using System.Collections.ObjectModel;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelineDeviceCardViewModel : ObservableObject
{
    private readonly PipelinesWorkspaceViewModel _workspace;
    private readonly PipelineEditorViewModel? _owner;
    public DeviceItem Device { get; }
    public string Id => Device.Id;
    public string AssignmentAction => L10n.Get(_owner is null ? "Mode.Assign" : "Mode.Detach");
    public bool CanChangeAssignment => Device.IsConnected && (_owner?.CanEdit ?? (_workspace.Selected is { } selected && PipelineDeviceBindingEditor.CanAssign(selected, Device.Device)));
    public AsyncCommand AssignmentCommand { get; }
    public PipelineDeviceCardViewModel(PipelinesWorkspaceViewModel workspace, DeviceItem device, PipelineEditorViewModel? owner = null)
    {
        _workspace = workspace;
        _owner = owner;
        Device = device;
        AssignmentCommand = new(() =>
        {
            if (_owner is not null)
                PipelineDeviceBindingEditor.Detach(_owner, Id);
            else if (_workspace.Selected is { } selected)
                PipelineDeviceBindingEditor.Assign(selected, Device.Device);
            return Task.CompletedTask;
        }, workspace.ReportError, () => CanChangeAssignment);
    }
    internal void Refresh()
    {
        Raise(nameof(AssignmentAction));
        Raise(nameof(CanChangeAssignment));
        AssignmentCommand.NotifyCanExecuteChanged();
    }
}

public sealed class PipelineDeviceGroupViewModel : ObservableObject
{
    private bool _selected;
    public PipelineEditorViewModel Editor { get; }
    public ObservableCollection<PipelineDeviceCardViewModel> Devices { get; } = [];
    public bool HasDevices => Devices.Count > 0;
    public bool IsSelected { get => _selected; internal set => Set(ref _selected, value); }
    public ActionCommand SelectCommand { get; }
    public PipelineDeviceGroupViewModel(PipelinesWorkspaceViewModel workspace, PipelineEditorViewModel editor)
    {
        Editor = editor;
        SelectCommand = new(() => workspace.Selected = editor);
    }
    internal void Refresh()
    {
        Raise(nameof(HasDevices));
        foreach (var device in Devices)
            device.Refresh();
    }
}
