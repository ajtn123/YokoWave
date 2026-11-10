using System.Globalization;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

/// <summary>Editable draft and preview, independent of playback selection and persistence.</summary>
public sealed class WaveformEditorViewModel : ObservableObject
{
    private string _name = "";
    private string _points = "0, 0.2, 0.8, 1, 0.8, 0.2, 0";
    private WaveShape _shape = WaveShape.Sine;
    private double _period = 4;
    private double _frequency = 20;
    private Guid? _editingId;
    private WaveformAsset? _original;
    private string _nameLanguage = L10n.Current.Language;
    public IReadOnlyList<ShapeItem> Shapes { get; } = Enum.GetValues<WaveShape>().Select(shape => new ShapeItem(shape)).ToArray();
    public WaveformSamplesEditorViewModel Samples { get; private set; }
    public string Name
    {
        get => _name;
        set
        {
            Set(ref _name, value);
            Raise(nameof(Preview));
        }
    }
    public WaveShape Shape
    {
        get => _shape;
        set
        {
            Set(ref _shape, value);
            Raise(nameof(SelectedShape));
            Raise(nameof(Preview));
            Raise(nameof(IsSampled));
            Raise(nameof(IsGenerated));
        }
    }
    public bool IsSampled => Shape == WaveShape.Sampled;
    public bool IsGenerated => !IsSampled;
    public ShapeItem? SelectedShape
    {
        get => Shapes.FirstOrDefault(shape => shape.Shape == Shape);
        set
        {
            if (value is not null)
                Shape = value.Shape;
        }
    }
    public double PeriodSeconds
    {
        get => _period;
        set
        {
            Set(ref _period, value);
            Raise(nameof(Preview));
        }
    }
    public double FrequencyHz
    {
        get => _frequency;
        set
        {
            Set(ref _frequency, value);
            Raise(nameof(Preview));
        }
    }
    public string PointsText
    {
        get => _points;
        set
        {
            Set(ref _points, value);
            Raise(nameof(Preview));
        }
    }
    public WaveformAsset? Preview
    {
        get
        {
            try
            {
                var waveform = BuildWaveform();
                return waveform.TryValidate(out _) ? waveform : null;
            }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
            {
                return null;
            }
        }
    }
    public WaveformEditorViewModel() => Samples = CreateSamples([new(0, 20), new(0, 20)]);
    public void Clear()
    {
        _editingId = null;
        _original = null;
        _nameLanguage = L10n.Current.Language;
        Name = "";
        Samples = CreateSamples([new(0, 20), new(0, 20)]);
        Raise(nameof(Samples));
        Shape = WaveShape.Sine;
        PeriodSeconds = 4;
        FrequencyHz = 20;
    }
    public void Load(WaveformItem selected)
    {
        var waveform = selected.Asset;
        _original = waveform;
        _nameLanguage = L10n.Current.Language;
        _editingId = selected.IsBuiltIn ? null : waveform.Id;
        Name = selected.IsBuiltIn ? L10n.Format("Wave.CopyName", selected.Name) : selected.Name;
        Shape = waveform.Shape;
        Samples = CreateSamples(waveform.Samples ?? [new(0, 20), new(0, 20)]);
        Raise(nameof(Samples));
        PeriodSeconds = waveform.PeriodSeconds;
        FrequencyHz = waveform.Frequency;
        PointsText = string.Join(", ", waveform.Points?.Select(point => point.ToString("0.###", CultureInfo.InvariantCulture)) ?? ["0", "1", "0"]);
    }
    internal void MarkSaved(Guid id) => _editingId = id;
    internal WaveformAsset BuildWaveform()
    {
        double[]? points = Shape == WaveShape.Custom ? PointsText.Split([',', '，', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray() : null;
        var samples = IsSampled ? Samples.Snapshot() : null;
        var names = _original?.Names?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (names is not null)
            names[_nameLanguage] = Name.Trim();
        return new(_editingId ?? Guid.NewGuid(), Name.Trim(), Shape, PeriodSeconds, FrequencyHz, points, samples)
        {
            Names = names,
            Source = _original?.Source
        };
    }

    private WaveformSamplesEditorViewModel CreateSamples(IEnumerable<WaveformSample> samples) => new(samples, () => Raise(nameof(Preview)));
}
