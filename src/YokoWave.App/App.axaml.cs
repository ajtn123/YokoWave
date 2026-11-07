using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YokoWave.App.ViewModels;
using YokoWave.App.Views;
using YokoWave.Core;

namespace YokoWave.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var dataDirectory = Environment.GetEnvironmentVariable("YOKOWAVE_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YokoWave");
        var viewModel = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(dataDirectory));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            if (desktop.Args?.Contains("--smoke-test") == true)
            {
                window.Opened += async (_, _) =>
                {
                    try
                    {
                        for (var attempt = 0; attempt < 100 && viewModel.Devices.Count == 0; attempt++)
                            await Task.Delay(50);
                        if (viewModel.Devices.Count == 0 || viewModel.IsError)
                            throw new InvalidOperationException(viewModel.Status);
                        Console.WriteLine("YokoWave native startup completed.");
                        window.Close();
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error);
                        desktop.Shutdown(1);
                    }
                };
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = viewModel };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
