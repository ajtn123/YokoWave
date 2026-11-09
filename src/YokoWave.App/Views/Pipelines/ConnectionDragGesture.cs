using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Nodify.Avalonia.Helpers.Gestures;

namespace YokoWave.App.Views.Pipelines;

internal sealed class ConnectionDragGesture(InputGesture original) : InputGesture
{
    public override bool Matches(object? source, RoutedEventArgs args)
    {
        if (original.Matches(source, args))
            return true;
        // Rapid consecutive drags can arrive as multi-click presses.
        return args is PointerPressedEventArgs { ClickCount: > 1, KeyModifiers: KeyModifiers.None } pressed
            && pressed.GetCurrentPoint(null).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
            && !KeyboardState.IsAnyKeyPressed(source as Visual ?? args.Source as Visual);
    }
}
