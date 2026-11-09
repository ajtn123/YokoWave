using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace YokoWave.App.Controls;

public sealed class ElectricalPad : Control
{
    public static readonly StyledProperty<double> FrequencyProperty = AvaloniaProperty.Register<ElectricalPad, double>(nameof(Frequency), 20, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> PulseWidthProperty = AvaloniaProperty.Register<ElectricalPad, double>(nameof(PulseWidth), 50, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsHeldProperty = AvaloniaProperty.Register<ElectricalPad, bool>(nameof(IsHeld), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IBrush?> PadBrushProperty = AvaloniaProperty.Register<ElectricalPad, IBrush?>(nameof(PadBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<ElectricalPad, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> CursorBrushProperty = AvaloniaProperty.Register<ElectricalPad, IBrush?>(nameof(CursorBrush));
    public static readonly StyledProperty<double> CursorRadiusProperty = AvaloniaProperty.Register<ElectricalPad, double>(nameof(CursorRadius));
    public double Frequency { get => GetValue(FrequencyProperty); set => SetValue(FrequencyProperty, value); }
    public double PulseWidth { get => GetValue(PulseWidthProperty); set => SetValue(PulseWidthProperty, value); }
    public bool IsHeld { get => GetValue(IsHeldProperty); set => SetValue(IsHeldProperty, value); }
    public IBrush? PadBrush { get => GetValue(PadBrushProperty); set => SetValue(PadBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? CursorBrush { get => GetValue(CursorBrushProperty); set => SetValue(CursorBrushProperty, value); }
    public double CursorRadius { get => GetValue(CursorRadiusProperty); set => SetValue(CursorRadiusProperty, value); }
    static ElectricalPad() => AffectsRender<ElectricalPad>(FrequencyProperty, PulseWidthProperty, IsHeldProperty, PadBrushProperty, GridBrushProperty, CursorBrushProperty, CursorRadiusProperty);
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Pointer.Capture(this);
        UpdatePoint(e.GetPosition(this));
        SetCurrentValue(IsHeldProperty, true);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (IsHeld)
            UpdatePoint(e.GetPosition(this));
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        SetCurrentValue(IsHeldProperty, false);
        e.Pointer.Capture(null);
        e.Handled = true;
        base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        SetCurrentValue(IsHeldProperty, false);
        base.OnPointerCaptureLost(e);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SetCurrentValue(IsHeldProperty, false);
        base.OnDetachedFromVisualTree(e);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (PadBrush is { } background)
            context.FillRectangle(background, bounds);
        var grid = new Pen(GridBrush, 1);
        for (int part = 0; part <= 4; part++)
        {
            context.DrawLine(grid, new(Bounds.Width * part / 4, 0), new(Bounds.Width * part / 4, Bounds.Height));
            context.DrawLine(grid, new(0, Bounds.Height * part / 4), new(Bounds.Width, Bounds.Height * part / 4));
        }
        var position = new Point((Frequency - 1) / 99 * Bounds.Width, (1 - PulseWidth / 100) * Bounds.Height);
        context.DrawEllipse(CursorBrush, null, position, CursorRadius, CursorRadius);
    }
    private void UpdatePoint(Point point)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        SetCurrentValue(FrequencyProperty, Math.Round(1 + 99 * Math.Clamp(point.X / Bounds.Width, 0, 1)));
        SetCurrentValue(PulseWidthProperty, Math.Round(100 * (1 - Math.Clamp(point.Y / Bounds.Height, 0, 1))));
    }
}
