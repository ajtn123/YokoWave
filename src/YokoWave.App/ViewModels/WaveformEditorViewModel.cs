using System.Globalization;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

/// <summary>Editable draft and preview, independent of playback selection and persistence.</summary>
public sealed class WaveformEditorViewModel : ObservableObject
{
    private string _name = "", _points = "0, 0.2, 0.8, 1, 0.8, 0.2, 0", _samples = "[]";
    private WaveShape _shape = WaveShape.Sine;
    private double _period = 4, _frequency = 20;
    private Guid? _editingId;
    public IReadOnlyList<ShapeItem> Shapes { get; } = Enum.GetValues<WaveShape>().Select(shape => new ShapeItem(shape)).ToArray();
    public string EditorName { get => _name; set { Set(ref _name, value); Raise(nameof(EditorPreview)); } }
    public WaveShape EditorShape
    {
        get => _shape;
        set
        {
            Set(ref _shape, value); Raise(nameof(SelectedShape)); Raise(nameof(EditorPreview));
            Raise(nameof(IsSampledEditor)); Raise(nameof(IsGeneratedEditor));
        }
    }
    public bool IsSampledEditor => EditorShape == WaveShape.Sampled;
    public bool IsGeneratedEditor => !IsSampledEditor;
    public ShapeItem? SelectedShape { get => Shapes.FirstOrDefault(shape => shape.Shape == EditorShape); set { if (value is not null) EditorShape = value.Shape; } }
    public double EditorPeriod { get => _period; set { Set(ref _period, value); Raise(nameof(EditorPreview)); } }
    public double EditorFrequency { get => _frequency; set { Set(ref _frequency, value); Raise(nameof(EditorPreview)); } }
    public string EditorPoints { get => _points; set { Set(ref _points, value); Raise(nameof(EditorPreview)); } }
    public string EditorSamples { get => _samples; set { Set(ref _samples, value); Raise(nameof(EditorPreview)); } }
    public WaveformPreset? EditorPreview
    {
        get
        {
            try { var preset = BuildPreset(); return preset.TryValidate(out _) ? preset : null; }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or JsonException) { return null; }
        }
    }
    public void Clear()
    {
        _editingId = null; EditorName = ""; EditorSamples = "[]";
        EditorShape = WaveShape.Sine; EditorPeriod = 4; EditorFrequency = 20;
    }
    public void Load(PresetItem selected)
    {
        var preset = selected.Preset; _editingId = selected.IsBuiltIn ? null : preset.Id;
        EditorName = selected.IsBuiltIn ? L10n.Format("Wave.CopyName", preset.Name) : preset.Name;
        EditorShape = preset.Shape;
        EditorSamples = JsonSerializer.Serialize(preset.Samples ?? [], new JsonSerializerOptions { WriteIndented = true });
        EditorPeriod = preset.PeriodSeconds; EditorFrequency = preset.Frequency;
        EditorPoints = string.Join(", ", preset.Points?.Select(point => point.ToString("0.###", CultureInfo.InvariantCulture)) ?? ["0", "1", "0"]);
    }
    internal void MarkSaved(Guid id) => _editingId = id;
    internal WaveformPreset BuildPreset()
    {
        var points = EditorShape == WaveShape.Custom ? EditorPoints.Split([',', '，', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray() : null;
        var samples = IsSampledEditor ? JsonSerializer.Deserialize<WaveformSample[]>(EditorSamples,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
        return new(_editingId ?? Guid.NewGuid(), EditorName.Trim(), EditorShape, EditorPeriod, EditorFrequency, points, samples);
    }
}
