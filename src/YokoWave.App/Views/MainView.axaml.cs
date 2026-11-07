using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using YokoWave.App.ViewModels;

namespace YokoWave.App.Views;

public sealed partial class MainView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _initialized;
    public MainView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => { if (DataContext is MainViewModel vm) await vm.TickAsync(); };
        Loaded += async (_, _) =>
        {
            if (!_initialized && DataContext is MainViewModel vm)
            {
                _initialized = true;
                try { await vm.InitializeAsync(); }
                catch (Exception e) { vm.ShowError(e); }
            }
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var compact = Bounds.Width < 1000;
        Sidebar.IsVisible = !compact;
        Shell.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 194);
        CompactNav.IsVisible = compact;
        ModeBadge.IsVisible = Bounds.Width > 800;
    }

}
