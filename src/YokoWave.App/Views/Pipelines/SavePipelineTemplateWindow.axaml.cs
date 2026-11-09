using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YokoWave.App.Views.Pipelines;

public sealed partial class SavePipelineTemplateWindow : Window
{
    public SavePipelineTemplateWindow()
    {
        InitializeComponent();
        Opened += (_, _) => TemplateNameInput.Focus();
    }
    private void Save(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel(object? sender, RoutedEventArgs e) => Close(false);
}
