using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace YokoWave.Tests.Support.Ui;

internal static class UiActions
{
    public static void Click(Window window, Control control)
    {
        foreach (var viewport in control.GetVisualAncestors().OfType<ScrollViewer>().Reverse())
        {
            viewport.BringIntoView();
            Dispatcher.UIThread.RunJobs();
        }
        control.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var position = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(position);
        window.MouseMove(position.Value);
        window.MouseDown(position.Value, MouseButton.Left);
        window.MouseUp(position.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public static Task EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null,
        [CallerArgumentExpression(nameof(condition))] string? expression = null) =>
        AsyncAssert.EventuallyAsync(condition, timeout, () => Dispatcher.UIThread.RunJobs(), expression);
}
