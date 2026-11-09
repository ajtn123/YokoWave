using Avalonia.Controls;
using Avalonia.Interactivity;
using YokoWave.App.ViewModels;

namespace YokoWave.App.Views.Pages;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private async void ClearLocalData(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings || !settings.ClearLocalDataCommand.CanExecute(null) || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        if (await new ClearLocalDataWindow().ShowDialog<bool>(owner))
            settings.ClearLocalDataCommand.Execute(null);
    }
}
