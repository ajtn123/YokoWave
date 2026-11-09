using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class SentStrengthViewModel(int channel) : ObservableObject
{
    private double _value;
    private bool _unknown;
    public int Channel { get; } = channel;
    public string ChannelName => L10n.Get("Channel." + (char)('A' + Channel));
    public string Label => IsUnknown ? L10n.Get("Pipeline.OutputUnknown") : L10n.Format("Pipeline.SentStrength", ChannelName);
    public double Value { get => _value; private set => Set(ref _value, value); }
    public bool IsUnknown { get => _unknown; private set => Set(ref _unknown, value); }
    public void Update(double value, bool unknown)
    {
        Value = Math.Clamp(value, 0, 100);
        IsUnknown = unknown;
        Raise(nameof(ChannelName));
        Raise(nameof(Label));
    }
}
