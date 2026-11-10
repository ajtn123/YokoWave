using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class WaveformItem(WaveformAsset waveform) : ObservableObject
{
    public WaveformAsset Asset => waveform;
    public string Name => waveform.GetName(L10n.Current.Culture);
    public string Detail => waveform.Shape == WaveShape.Sampled
        ? L10n.Format("Wave.SampledDetail", waveform.Samples?.Length ?? 0, waveform.PeriodSeconds)
        : L10n.Format("Wave.Detail", waveform.PeriodSeconds, waveform.Frequency);
    public string ShapeLabel => L10n.Get($"Shape.{waveform.Shape}");
    public bool IsBuiltIn => DefaultWaveformCatalog.Contains(waveform.Id);
    public bool HasSource => waveform.Source is not null;
    public string Origin => waveform.Source is { } source ? $"{source.Provider} · {source.Collection}" : L10n.Get(IsBuiltIn ? "Wave.BuiltIn" : "Wave.CustomOrigin");
    public override string ToString() => Name;
    public void RefreshLocalization()
    {
        foreach (string? property in new[] { nameof(Asset), nameof(Name), nameof(Detail), nameof(ShapeLabel), nameof(Origin) })
            Raise(property);
    }
}
