using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YokoWave.App.Views.Pipelines;

public sealed partial class PipelineEditorView : UserControl
{
    public PipelineEditorView() => InitializeComponent();
    private void FitGraph(object? sender, RoutedEventArgs e) => Canvas.FitToScreen();
}
