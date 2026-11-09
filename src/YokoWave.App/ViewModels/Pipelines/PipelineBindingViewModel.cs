using System.Collections.ObjectModel;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelineBindingViewModel(PipelineEditorViewModel editor, string id) : ObservableObject
{
    public string Id { get; } = id;
    public ObservableCollection<PipelineDeviceOption> Devices => editor.Devices;
    public PipelineDeviceOption? Selected
    {
        get => Devices.FirstOrDefault(d => d.Id == Id);
        set
        {
            if (value is not null && value.Id != Id)
                editor.RebindDevice(Id, value.Id);
        }
    }
}
