using Avalonia;
using Avalonia.Controls;

namespace YokoWave.App.Views.Pages;

public sealed partial class DeviceWorkspaceView : UserControl
{
    public DeviceWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
        DevicePane.SizeChanged += (_, _) => ConstrainControlPane();
    }

    private void UpdateLayoutMode()
    {
        var stacked = Bounds.Width < (double)this.FindResource("Layout.WorkspaceBreakpoint")!;
        WorkspaceGrid.ColumnDefinitions = stacked
            ? new ColumnDefinitions("*")
            : [new(new GridLength((double)this.FindResource("Layout.DevicePaneWidth")!)), new(GridLength.Star)];
        WorkspaceGrid.RowDefinitions = RowDefinitions.Parse(stacked ? "Auto,Auto" : "Auto");
        Grid.SetColumn(ControlPane, stacked ? 0 : 1);
        Grid.SetRow(ControlPane, stacked ? 1 : 0);
        ConstrainControlPane();
    }

    private void ConstrainControlPane()
    {
        if (Bounds.Width <= 0) return;
        var spacing = (double)this.FindResource("Space.Section")!;
        ControlPane.MaxWidth = Grid.GetRow(ControlPane) == 1 ? Bounds.Width
            : Math.Max(0, Bounds.Width - DevicePane.Bounds.Width - spacing);
    }
}
