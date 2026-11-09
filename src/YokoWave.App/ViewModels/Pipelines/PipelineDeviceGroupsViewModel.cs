using System.Collections.ObjectModel;

namespace YokoWave.App.ViewModels.Pipelines;

/// <summary>Reconciles connected device cards with graph membership without changing graph topology.</summary>
public sealed class PipelineDeviceGroupsViewModel(
    Func<PipelineEditorViewModel?> selected, Action<PipelineEditorViewModel> select, Action<Exception> onError) : ObservableObject
{
    public ObservableCollection<PipelineDeviceGroupViewModel> Groups { get; } = [];
    public ObservableCollection<PipelineDeviceCardViewModel> UnassignedDevices { get; } = [];
    public bool HasUnassignedDevices => UnassignedDevices.Count > 0;

    internal void Refresh(IReadOnlyList<PipelineEditorViewModel> editors, IReadOnlyList<DeviceItem> connected)
    {
        foreach (var removed in Groups.Where(group => !editors.Contains(group.Editor)).ToArray())
            Groups.Remove(removed);
        foreach (var editor in editors)
        {
            var group = Groups.FirstOrDefault(candidate => candidate.Editor == editor);
            if (group is null)
            {
                group = new(editor, select);
                Groups.Add(group);
            }
            group.IsSelected = editor == selected();
            SynchronizeCards(group.Devices, connected.Where(device => editor.AssignedDeviceIds.Contains(device.Id)).ToArray(), editor);
            group.Refresh();
        }
        var assigned = editors.SelectMany(editor => editor.AssignedDeviceIds).ToHashSet(StringComparer.Ordinal);
        SynchronizeCards(UnassignedDevices, connected.Where(device => !assigned.Contains(device.Id)).ToArray(), null);
        foreach (var device in UnassignedDevices)
            device.Refresh();
        Raise(nameof(HasUnassignedDevices));
    }

    private void SynchronizeCards(ObservableCollection<PipelineDeviceCardViewModel> cards, DeviceItem[] devices, PipelineEditorViewModel? owner)
    {
        foreach (var removed in cards.Where(card => !devices.Contains(card.Device)).ToArray())
            cards.Remove(removed);
        foreach (var device in devices)
        {
            if (cards.All(card => card.Device != device))
                cards.Add(new(device, selected, onError, owner));
        }
    }
}
