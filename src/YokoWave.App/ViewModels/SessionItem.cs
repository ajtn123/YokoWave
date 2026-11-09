using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class SessionItem(SessionRecord record) : ObservableObject
{
    public SessionRecord Record { get; } = record;
    public string Name => Record.PipelineName;
    public string Date => Record.StartedAt.ToLocalTime().ToString(L10n.Get("History.DateFormat"), L10n.Current.Culture);
    public string Duration => TimeSpan.FromSeconds(Record.DurationSeconds).ToString(Record.DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
    public string Detail
    {
        get
        {
            string Peak(double? value) => value is { } number ? number.ToString("0.#", L10n.Current.Culture) + "%" : L10n.Get("History.UnknownOutput");
            string detail = L10n.Format("History.PipelineDetail", Record.DeviceCount, Peak(Record.PeakA), Peak(Record.PeakB), Peak(Record.PeakC), new StoredReason(Record.Reason));
            string[] waves = (Record.Contents ?? []).Where(c => c.WaveformName is not null).Select(c => c.WaveformId is { } id && DefaultWaveformCatalog.Contains(id)
                ? DefaultWaveformCatalog.Name(id) : c.WaveformName!).Distinct().ToArray();
            return waves.Length > 0 ? L10n.Format("History.PipelineWaves", detail, string.Join(", ", waves)) : detail;
        }
    }
    public void RefreshLocalization()
    {
        Raise(nameof(Name));
        Raise(nameof(Date));
        Raise(nameof(Detail));
    }
}
