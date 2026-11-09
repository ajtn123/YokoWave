using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using YokoWave.App.ViewModels.Pipelines;

namespace YokoWave.App.Views.Pipelines;

public sealed partial class PipelinesWorkspaceView : UserControl
{
    public PipelinesWorkspaceView() => InitializeComponent();

    private void SelectContextPipeline(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is PipelinesWorkspaceViewModel workspace && sender is Border { DataContext: PipelineDeviceGroupViewModel group })
            workspace.Selected = group.Editor;
    }

    private async void SaveTemplate(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PipelinesWorkspaceViewModel workspace || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        try
        {
            var dialog = new SavePipelineTemplateWindow { DataContext = workspace };
            if (await dialog.ShowDialog<bool>(owner))
                await workspace.SaveTemplateCommand.ExecuteAsync();
        }
        catch (Exception error)
        {
            workspace.ReportError(error);
        }
    }
}
