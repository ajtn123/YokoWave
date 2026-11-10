using System.Collections.ObjectModel;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public abstract class DeviceOutputEditorViewModel : NodeEditorViewModel
{
    private string _deviceId;
    private readonly ObservableCollection<SentStrengthViewModel> _sentStrength = [];
    public override IReadOnlyList<SentStrengthViewModel> SentStrength => _sentStrength;
    protected virtual IEnumerable<int> MeterChannels => HasStrengthLimit && Editor.FindDevice(DeviceId) is { IsInput: false, Kind: not Core.DeviceKind.Pump } device
        ? Enumerable.Range(0, 3).Where(device.HasChannel) : [];
    public override string Summary => SelectedDevice?.Name ?? DeviceId;
    public string DeviceId => _deviceId;
    public virtual IReadOnlyList<PipelineDeviceOption> Devices => Editor.Devices;
    public PipelineDeviceOption? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Id == _deviceId);
        set
        {
            if (value is not null && value.Id != _deviceId)
            {
                Change(() =>
                {
                    _deviceId = value.Id;
                    Raise();
                });
                RefreshDevices();
            }
        }
    }
    protected DeviceOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition, string deviceId) : base(editor, definition) => _deviceId = deviceId;
    internal virtual void RefreshDevices()
    {
        Raise(nameof(Devices));
        Raise(nameof(SelectedDevice));
    }
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        int[] channels = MeterChannels.ToArray();
        if (!_sentStrength.Select(meter => meter.Channel).SequenceEqual(channels))
        {
            _sentStrength.Clear();
            foreach (int channel in channels)
                _sentStrength.Add(new(channel));
        }
        var (frame, unknown) = Editor.ReadSentOutput(DeviceId);
        foreach (var meter in _sentStrength)
            meter.Update(meter.Channel == 0 ? frame.ChannelA : meter.Channel == 1 ? frame.ChannelB : frame.ChannelC, unknown);
    }
}
