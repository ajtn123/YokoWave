using Avalonia.Controls;

namespace YokoWave.App.Views.Pages;

public sealed partial class DeviceWorkspaceView : UserControl
{
    public DeviceWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var stacked = Bounds.Width < 740;
        WorkspaceGrid.ColumnDefinitions = ColumnDefinitions.Parse(stacked ? "*" : "264,22,*");
        WorkspaceGrid.RowDefinitions = RowDefinitions.Parse(stacked ? "Auto,24,Auto" : "Auto");
        Grid.SetColumn(ControlPane, stacked ? 0 : 2);
        Grid.SetRow(ControlPane, stacked ? 2 : 0);
    }
}
