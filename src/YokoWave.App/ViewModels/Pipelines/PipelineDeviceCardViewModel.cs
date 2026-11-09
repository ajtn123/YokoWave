using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelineDeviceCardViewModel : ObservableObject
{
    private readonly Func<PipelineEditorViewModel?> _selected;
    private readonly PipelineEditorViewModel? _owner;
    public DeviceItem Device { get; }
    public string Id => Device.Id;
    public string AssignmentAction => L10n.Get(_owner is null ? "Mode.Assign" : "Mode.Detach");
    public bool CanChangeAssignment => Device.IsConnected && (_owner?.CanEdit ?? (_selected() is { } selected && PipelineDeviceBindingEditor.CanAssign(selected, Device.Device)));
    public AsyncCommand AssignmentCommand { get; }
    public PipelineDeviceCardViewModel(DeviceItem device, Func<PipelineEditorViewModel?> selected, Action<Exception> onError, PipelineEditorViewModel? owner = null)
    {
        _selected = selected;
        _owner = owner;
        Device = device;
        AssignmentCommand = new(() =>
        {
            if (_owner is not null)
                PipelineDeviceBindingEditor.Detach(_owner, Id);
            else if (_selected() is { } editor)
                PipelineDeviceBindingEditor.Assign(editor, Device.Device);
            return Task.CompletedTask;
        }, onError, () => CanChangeAssignment);
    }
    internal void Refresh()
    {
        Raise(nameof(AssignmentAction));
        Raise(nameof(CanChangeAssignment));
        AssignmentCommand.NotifyCanExecuteChanged();
    }
}
