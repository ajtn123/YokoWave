using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace YokoWave.App.Controls;

public sealed class HoldButton : Button
{
    public static readonly StyledProperty<bool> IsHeldProperty = AvaloniaProperty.Register<HoldButton, bool>(nameof(IsHeld), defaultBindingMode: BindingMode.TwoWay);
    private Window? _window;
    public bool IsHeld { get => GetValue(IsHeldProperty); set => SetValue(IsHeldProperty, value); }
    protected override Type StyleKeyOverride => typeof(Button);
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
            _window.Deactivated += Release;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Release(this, EventArgs.Empty);
        if (_window is not null)
            _window.Deactivated -= Release;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsEffectivelyEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            SetCurrentValue(IsHeldProperty, true);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        SetCurrentValue(IsHeldProperty, false);
        base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        SetCurrentValue(IsHeldProperty, false);
        base.OnPointerCaptureLost(e);
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        SetCurrentValue(IsHeldProperty, false);
        base.OnLostFocus(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsEffectivelyEnabled && e.Key is Key.Space or Key.Enter)
            SetCurrentValue(IsHeldProperty, true);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
            SetCurrentValue(IsHeldProperty, false);
        base.OnKeyUp(e);
    }
    private void Release(object? sender, EventArgs e) => SetCurrentValue(IsHeldProperty, false);
}
