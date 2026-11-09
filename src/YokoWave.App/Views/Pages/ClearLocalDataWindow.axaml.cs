using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YokoWave.App.Views.Pages;

public sealed partial class ClearLocalDataWindow : Window
{
    public ClearLocalDataWindow() => InitializeComponent();
    private void Confirm(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
