using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using YokoWave.Core;

namespace YokoWave.App.Controls;

public sealed class WaveformPlot : Control
{
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
    public WaveformPreset? Preset { get => GetValue(PresetProperty); set => SetValue(PresetProperty, value); }
    public WaveformPreset? PresetB { get => GetValue(PresetBProperty); set => SetValue(PresetBProperty, value); }
    public double AmplitudeA { get => GetValue(AmplitudeAProperty); set => SetValue(AmplitudeAProperty, value); }
    public double AmplitudeB { get => GetValue(AmplitudeBProperty); set => SetValue(AmplitudeBProperty, value); }
    public bool Offset { get => GetValue(OffsetProperty); set => SetValue(OffsetProperty, value); }
    public double Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? ChannelABrush { get => GetValue(ChannelABrushProperty); set => SetValue(ChannelABrushProperty, value); }
    public IBrush? ChannelBBrush { get => GetValue(ChannelBBrushProperty); set => SetValue(ChannelBBrushProperty, value); }
    public IBrush? AreaBrush { get => GetValue(AreaBrushProperty); set => SetValue(AreaBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 1 || height < 1) return;
        using var clip = context.PushClip(new Rect(Bounds.Size));
        if (ShowGrid)
        {
            var grid = new Pen(GridBrush, 1, new DashStyle([2, 6], 0));
            for (var i = 0; i <= 4; i++) context.DrawLine(grid, new(0, i * height / 4), new(width, i * height / 4));
            for (var i = 0; i <= 12; i++) context.DrawLine(grid, new(i * width / 12, 0), new(i * width / 12, height));
        }
        var preset = Preset;
        if (preset is null) return;
        DrawWave(context, PresetB ?? preset, width, height, AmplitudeB, PresetB is null && Offset ? 0.5 : 0, ChannelBBrush, false);
        DrawWave(context, preset, width, height, AmplitudeA, 0, ChannelABrush, true);
        if (Playhead > 0)
        {
            var x = Playhead * width;
            context.DrawLine(new Pen(PlayheadBrush, 1), new(x, 0), new(x, height));
            context.DrawEllipse(PlayheadBrush, null, new(x, 4), 3, 3);
        }
    }

    private void DrawWave(DrawingContext context, WaveformPreset preset, double width, double height,
        double amplitude, double phase, IBrush? brush, bool fill)
    {
        var geometry = new StreamGeometry();
        var baseline = height - 4;
        var points = new List<Point>();
        var count = Math.Max(100, (int)width);
        for (var i = 0; i <= count; i++)
        {
            var x = i * width / count;
            var value = WaveformGenerator.Sample(preset, i * (ShowGrid ? 12d : preset.PeriodSeconds * 2) / count, phase);
            points.Add(new(x, baseline - value * Math.Clamp(amplitude, 0, 100) / 100 * (height - 10)));
        }
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], false);
            foreach (var point in points.Skip(1)) path.LineTo(point);
            path.EndFigure(false);
        }
        if (fill)
        {
            var area = new StreamGeometry();
            using (var path = area.Open())
            {
                path.BeginFigure(new(0, baseline), true);
                foreach (var point in points) path.LineTo(point);
                path.LineTo(new(width, baseline)); path.EndFigure(true);
            }
            context.DrawGeometry(AreaBrush, null, area);
        }
        context.DrawGeometry(null, new Pen(brush, ShowGrid ? 2.5 : 1.8), geometry);
    }
}
