using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class WaveformItem(WaveformAsset waveform) : ObservableObject
{
    public WaveformAsset Asset => IsBuiltIn ? waveform with { Name = Name } : waveform;
    public string Name => IsBuiltIn ? DefaultWaveformCatalog.Name(waveform.Id) : waveform.Name;
    public string Detail => waveform.Shape == WaveShape.Sampled
        ? L10n.Format("Wave.SampledDetail", waveform.Samples?.Length ?? 0, waveform.PeriodSeconds)
        : L10n.Format("Wave.Detail", waveform.PeriodSeconds, waveform.Frequency);
    public string ShapeLabel => L10n.Get($"Shape.{waveform.Shape}");
    public bool IsBuiltIn => DefaultWaveformCatalog.Contains(waveform.Id);
    public string Origin => L10n.Get(IsBuiltIn ? "Wave.BuiltIn" : "Wave.CustomOrigin");
    public override string ToString() => Name;
    public void RefreshLocalization()
    {
        foreach (string? property in new[] { nameof(Asset), nameof(Name), nameof(Detail), nameof(ShapeLabel), nameof(Origin) })
            Raise(property);
    }
}
