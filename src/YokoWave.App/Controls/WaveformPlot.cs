using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using YokoWave.Core;

namespace YokoWave.App.Controls;

public sealed class WaveformPlot : Control
{
    private readonly record struct CurveKey(Size Size, WaveformPreset Preset, WaveformPreset? PresetB,
        double AmplitudeA, double AmplitudeB, bool Offset, bool ShowGrid);
    private readonly record struct PenKey(IBrush? Grid, IBrush? A, IBrush? B, IBrush? Playhead, bool ShowGrid);
    private CurveKey? _curveKey;
    private PenKey? _penKey;
    private StreamGeometry? _curveA, _curveB, _area;
    private Pen? _gridPen, _penA, _penB, _playheadPen;
    public static readonly StyledProperty<WaveformPreset?> PresetProperty =
        AvaloniaProperty.Register<WaveformPlot, WaveformPreset?>(nameof(Preset));
    public static readonly StyledProperty<WaveformPreset?> PresetBProperty =
        AvaloniaProperty.Register<WaveformPlot, WaveformPreset?>(nameof(PresetB));
    public static readonly StyledProperty<double> AmplitudeAProperty =
        AvaloniaProperty.Register<WaveformPlot, double>(nameof(AmplitudeA), 70);
    public static readonly StyledProperty<double> AmplitudeBProperty =
        AvaloniaProperty.Register<WaveformPlot, double>(nameof(AmplitudeB), 45);
    public static readonly StyledProperty<bool> OffsetProperty =
        AvaloniaProperty.Register<WaveformPlot, bool>(nameof(Offset), true);
    public static readonly StyledProperty<double> PlayheadProperty =
        AvaloniaProperty.Register<WaveformPlot, double>(nameof(Playhead));
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<WaveformPlot, bool>(nameof(ShowGrid), true);
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<WaveformPlot, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> ChannelABrushProperty =
        AvaloniaProperty.Register<WaveformPlot, IBrush?>(nameof(ChannelABrush));
    public static readonly StyledProperty<IBrush?> ChannelBBrushProperty =
        AvaloniaProperty.Register<WaveformPlot, IBrush?>(nameof(ChannelBBrush));
    public static readonly StyledProperty<IBrush?> AreaBrushProperty =
        AvaloniaProperty.Register<WaveformPlot, IBrush?>(nameof(AreaBrush));
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<WaveformPlot, IBrush?>(nameof(PlayheadBrush));

    static WaveformPlot() => AffectsRender<WaveformPlot>(PresetProperty, PresetBProperty, AmplitudeAProperty, AmplitudeBProperty,
        OffsetProperty, PlayheadProperty, ShowGridProperty, GridBrushProperty, ChannelABrushProperty,
        ChannelBBrushProperty, AreaBrushProperty, PlayheadBrushProperty);
    public WaveformPreset? Preset
    {
        get => GetValue(PresetProperty); set => SetValue(PresetProperty, value);
    }
    public WaveformPreset? PresetB
    {
        get => GetValue(PresetBProperty); set => SetValue(PresetBProperty, value);
    }
    public double AmplitudeA
    {
        get => GetValue(AmplitudeAProperty); set => SetValue(AmplitudeAProperty, value);
    }
    public double AmplitudeB
    {
        get => GetValue(AmplitudeBProperty); set => SetValue(AmplitudeBProperty, value);
    }
    public bool Offset
    {
        get => GetValue(OffsetProperty); set => SetValue(OffsetProperty, value);
    }
    public double Playhead
    {
        get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value);
    }
    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value);
    }
    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value);
    }
    public IBrush? ChannelABrush
    {
        get => GetValue(ChannelABrushProperty); set => SetValue(ChannelABrushProperty, value);
    }
    public IBrush? ChannelBBrush
    {
        get => GetValue(ChannelBBrushProperty); set => SetValue(ChannelBBrushProperty, value);
    }
    public IBrush? AreaBrush
    {
        get => GetValue(AreaBrushProperty); set => SetValue(AreaBrushProperty, value);
    }
    public IBrush? PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 1 || height < 1)
            return;
        UpdatePens();
        using var clip = context.PushClip(new Rect(Bounds.Size));
        if (ShowGrid)
        {
            for (var i = 0; i <= 4; i++)
                context.DrawLine(_gridPen!, new(0, i * height / 4), new(width, i * height / 4));
            for (var i = 0; i <= 12; i++)
                context.DrawLine(_gridPen!, new(i * width / 12, 0), new(i * width / 12, height));
        }
        var preset = Preset;
        if (preset is null)
            return;
        var key = new CurveKey(Bounds.Size, preset, PresetB, AmplitudeA, AmplitudeB, Offset, ShowGrid);
        if (_curveKey != key)
        {
            (_curveB, _) = BuildCurve(PresetB ?? preset, width, height, AmplitudeB, PresetB is null && Offset ? .5 : 0, false);
            (_curveA, _area) = BuildCurve(preset, width, height, AmplitudeA, 0, true);
            _curveKey = key;
        }
        context.DrawGeometry(null, _penB, _curveB!);
        context.DrawGeometry(AreaBrush, null, _area!);
        context.DrawGeometry(null, _penA, _curveA!);
        if (Playhead > 0)
        {
            var x = Playhead * width;
            context.DrawLine(_playheadPen!, new(x, 0), new(x, height));
            context.DrawEllipse(PlayheadBrush, null, new(x, 4), 3, 3);
        }
    }

    private void UpdatePens()
    {
        var key = new PenKey(GridBrush, ChannelABrush, ChannelBBrush, PlayheadBrush, ShowGrid);
        if (_penKey == key)
            return;
        _gridPen = new(GridBrush, 1, new DashStyle([2, 6], 0));
        _penA = new(ChannelABrush, ShowGrid ? 2.5 : 1.8);
        _penB = new(ChannelBBrush, ShowGrid ? 2.5 : 1.8);
        _playheadPen = new(PlayheadBrush, 1);
        _penKey = key;
    }

    private (StreamGeometry Curve, StreamGeometry? Area) BuildCurve(WaveformPreset preset, double width, double height,
        double amplitude, double phase, bool fill)
    {
        var geometry = new StreamGeometry();
        var area = fill ? new StreamGeometry() : null;
        var baseline = height - 4;
        var count = Math.Max(100, (int)width);
        using (var path = geometry.Open())
        using (var areaPath = area?.Open())
        {
            areaPath?.BeginFigure(new(0, baseline), true);
            for (var i = 0; i <= count; i++)
            {
                var value = WaveformGenerator.Sample(preset, i * (ShowGrid ? 12d : preset.PeriodSeconds * 2) / count, phase);
                var point = new Point(i * width / count, baseline - value * Math.Clamp(amplitude, 0, 100) / 100 * (height - 10));
                if (i == 0)
                    path.BeginFigure(point, false);
                else
                    path.LineTo(point);
                areaPath?.LineTo(point);
            }
            path.EndFigure(false);
            areaPath?.LineTo(new(width, baseline));
            areaPath?.EndFigure(true);
        }
        return (geometry, area);
    }
}
