using System.Collections.ObjectModel;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelineDeviceGroupViewModel : ObservableObject
{
    private bool _selected;
    public PipelineEditorViewModel Editor { get; }
    public ObservableCollection<PipelineDeviceCardViewModel> Devices { get; } = [];
    public bool HasDevices => Devices.Count > 0;
    public bool IsSelected { get => _selected; internal set => Set(ref _selected, value); }
    public ActionCommand SelectCommand { get; }
    public PipelineDeviceGroupViewModel(PipelineEditorViewModel editor, Action<PipelineEditorViewModel> select)
    {
        Editor = editor;
        SelectCommand = new(() => select(editor));
    }
    internal void Refresh()
    {
        Raise(nameof(HasDevices));
        foreach (var device in Devices)
            device.Refresh();
    }
}
