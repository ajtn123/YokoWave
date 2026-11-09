using Avalonia.Controls;
using YokoWave.App.ViewModels;

namespace YokoWave.App.Views;

public sealed partial class MainView : UserControl
{
    private bool _initialized;
    public MainView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!_initialized && DataContext is MainViewModel vm)
            {
                _initialized = true;
                try
                {
                    await vm.InitializeAsync();
                }
                catch (Exception e) { vm.ShowError(e); }
            }
        };
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var compact = Bounds.Width < (double)this.FindResource("Layout.CompactBreakpoint")!;
        Sidebar.IsVisible = !compact;
        Shell.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : (double)this.FindResource("Layout.SidebarWidth")!);
        CompactNav.IsVisible = compact;
    }

}
