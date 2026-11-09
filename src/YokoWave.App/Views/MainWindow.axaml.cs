using Avalonia.Controls;
using Avalonia.Input;
using YokoWave.App.ViewModels;

namespace YokoWave.App.Views;

public sealed partial class MainWindow : Window
{
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is MainViewModel vm)
            {
                e.Handled = true;
                try
                {
                    await vm.StopAllAsync("Reason.Shortcut");
                }
                catch (Exception error) { vm.ShowError(error); }
            }
        };
        Closing += async (_, e) =>
        {
            if (_closing || DataContext is not MainViewModel vm)
                return;
            e.Cancel = true;
            try
            {
                await vm.DisposeAsync();
            }
            catch (Exception error) { vm.ShowError(error); }
            finally { _closing = true; Close(); }
        };
    }
}
