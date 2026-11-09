using System.Globalization;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

/// <summary>Editable draft and preview, independent of playback selection and persistence.</summary>
public sealed class WaveformEditorViewModel : ObservableObject
{
    private string _name = "", _points = "0, 0.2, 0.8, 1, 0.8, 0.2, 0";
    private WaveShape _shape = WaveShape.Sine;
    private double _period = 4, _frequency = 20;
    private Guid? _editingId;
    public IReadOnlyList<ShapeItem> Shapes { get; } = Enum.GetValues<WaveShape>().Select(shape => new ShapeItem(shape)).ToArray();
    public WaveformSamplesEditorViewModel Samples { get; private set; }
    public WaveformEditorViewModel() => Samples = CreateSamples([new(0, 20), new(0, 20)]);
    private WaveformSamplesEditorViewModel CreateSamples(IEnumerable<WaveformSample> samples) => new(samples, () => Raise(nameof(Preview)));
    public string Name
    {
        get => _name; set
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
        get => Shapes.FirstOrDefault(shape => shape.Shape == Shape); set
        {
            if (value is not null)
                Shape = value.Shape;
        }
    }
    public double PeriodSeconds
    {
        get => _period; set
        {
            Set(ref _period, value);
            Raise(nameof(Preview));
        }
    }
    public double FrequencyHz
    {
        get => _frequency; set
        {
            Set(ref _frequency, value);
            Raise(nameof(Preview));
        }
    }
    public string PointsText
    {
        get => _points; set
        {
            Set(ref _points, value);
            Raise(nameof(Preview));
        }
    }
    public WaveformPreset? Preview
    {
        get
        {
            try
            {
                var preset = BuildPreset();
                return preset.TryValidate(out _) ? preset : null;
            }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { return null; }
        }
    }
    public void Clear()
    {
        _editingId = null;
        Name = "";
        Samples = CreateSamples([new(0, 20), new(0, 20)]);
        Raise(nameof(Samples));
        Shape = WaveShape.Sine;
        PeriodSeconds = 4;
        FrequencyHz = 20;
    }
    public void Load(PresetItem selected)
    {
        var preset = selected.Preset;
        _editingId = selected.IsBuiltIn ? null : preset.Id;
        Name = selected.IsBuiltIn ? L10n.Format("Wave.CopyName", preset.Name) : preset.Name;
        Shape = preset.Shape;
        Samples = CreateSamples(preset.Samples ?? [new(0, 20), new(0, 20)]);
        Raise(nameof(Samples));
        PeriodSeconds = preset.PeriodSeconds;
        FrequencyHz = preset.Frequency;
        PointsText = string.Join(", ", preset.Points?.Select(point => point.ToString("0.###", CultureInfo.InvariantCulture)) ?? ["0", "1", "0"]);
    }
    internal void MarkSaved(Guid id) => _editingId = id;
    internal WaveformPreset BuildPreset()
    {
        var points = Shape == WaveShape.Custom ? PointsText.Split([',', '，', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray() : null;
        var samples = IsSampled ? Samples.Snapshot() : null;
        return new(_editingId ?? Guid.NewGuid(), Name.Trim(), Shape, PeriodSeconds, FrequencyHz, points, samples);
    }
}
