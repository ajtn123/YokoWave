using System.Collections.ObjectModel;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class ChannelOutputEditorViewModel : DeviceOutputEditorViewModel
{

    private int _channel;
    private readonly ChannelOutputNode _settings;
    protected override IEnumerable<int> MeterChannels => [Channel];
    public ObservableCollection<SessionOption> Channels { get; } = [];
    public SessionOption? SelectedChannel
    {
        get => Channels.FirstOrDefault(c => c.Value == Channel);
        set { if (value is not null) Channel = value.Value; }
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
    public ChannelOutputEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition,
        ((ChannelOutputNode)editor.Registry.Get(definition.Type).Read(definition.Configuration)).DeviceId)
    {
        _settings = (ChannelOutputNode)editor.Registry.Get(Type).Read(definition.Configuration);
        _channel = _settings.Channel;
        RefreshDevices();
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
