using YokoWave.Core;

namespace YokoWave.App.ViewModels;

public sealed class WaveformSampleRow : ObservableObject
{
    private readonly Action _changed;
    private readonly double[] _channels = [1, 1, 1];
    private WaveformSample _sample;
    private bool _useChannels;
    private int _channelCount = 3;
    public double Level { get => _sample.Level; set => Update(_sample with { Level = value }); }
    public double FrequencyHz { get => _sample.FrequencyHz; set => Update(_sample with { FrequencyHz = value }); }
    public double PulseWidth { get => _sample.PulseWidth; set => Update(_sample with { PulseWidth = value }); }
    public bool UseChannels
    {
        get => _useChannels;
        set { if (Set(ref _useChannels, value)) _changed(); }
    }
    public IReadOnlyList<SessionOption> ChannelCounts { get; } = Enumerable.Range(1, 3).Select(n => new SessionOption(n, "Wave.ChannelCount", n)).ToArray();
    public SessionOption ChannelCount
    {
        get => ChannelCounts.Single(c => c.Value == _channelCount);
        set
        {
            if (value is not null && Set(ref _channelCount, value.Value))
                _changed();
        }
    }
    public double ChannelA { get => _channels[0]; set => Channel(0, value); }
    public double ChannelB { get => _channels[1]; set => Channel(1, value); }
    public double ChannelC { get => _channels[2]; set => Channel(2, value); }
    public WaveformSampleRow(WaveformSample sample, Action changed)
    {
        _sample = sample;
        _changed = changed;
        if (sample.Channels is { } channels)
        {
            _useChannels = true;
            _channelCount = channels.Length;
            channels.CopyTo(_channels, 0);
        }
    }

    public WaveformSample Snapshot() => _sample with { Channels = UseChannels ? _channels.Take(_channelCount).ToArray() : null };

    private void Update(WaveformSample sample)
    {
        if (!sample.IsValid || sample == _sample)
            return;
        _sample = sample;
        Raise(nameof(Level));
        Raise(nameof(FrequencyHz));
        Raise(nameof(PulseWidth));
        _changed();
    }
    private void Channel(int channel, double value)
    {
        if (!double.IsFinite(value) || value is < -1 or > 1 || _channels[channel] == value)
            return;
        _channels[channel] = value;
        Raise(channel switch { 0 => nameof(ChannelA), 1 => nameof(ChannelB), _ => nameof(ChannelC) });
        _changed();
    }
}

public sealed class WaveformSamplesEditorViewModel(IEnumerable<WaveformSample> samples, Action changed)
    : OrderedRowsEditorViewModel<WaveformSampleRow>(samples.Select(s => new WaveformSampleRow(s, changed)), () => new(new(0, 20, 1), changed), 2, WaveformAsset.MaximumSamples, () => true, changed)
{
    public WaveformSample[] Snapshot() => Rows.Select(r => r.Snapshot()).ToArray();
}
