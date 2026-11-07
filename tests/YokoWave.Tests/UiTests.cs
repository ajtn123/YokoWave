using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YokoWave.App.ViewModels;
using YokoWave.App.Views;
using YokoWave.App.Controls;
using YokoWave.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(YokoWave.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace YokoWave.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .WithInterFont()
        .UseHarfBuzz()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task NativeWindowShortcutAndCloseStopAndPersistTheSession()
    {
        using var folder = new TestFolder();
        var transport = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.ChannelA = 20;
        await vm.TogglePlaybackAsync();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await WaitForAsync(() => !vm.IsRunning);
        Assert.All(transport.Outputs.Values, output => Assert.Equal(0, output.ChannelA));
        await vm.TogglePlaybackAsync();
        window.Close();
        await WaitForAsync(() => !window.IsVisible);
        Assert.Empty(transport.Outputs);
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(2, saved.Sessions.Count);
    }

    [AvaloniaFact]
    public async Task ThemeAndChartFollowLiveThemeAndAccentChanges()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var app = Application.Current!;
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1450, Height = 1100, RequestedThemeVariant = ThemeVariant.Light };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.ChannelA = 40; vm.ChannelB = 25;
        Dispatcher.UIThread.RunJobs();
        var lightCanvas = Assert.IsAssignableFrom<ISolidColorBrush>(view.Background).Color;
        var plot = view.FindControl<WaveformPlot>("MainPlot")!;
        var lightGrid = Assert.IsAssignableFrom<ISolidColorBrush>(plot.GridBrush).Color;
        Capture(window, "workspace-light.png");
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(lightCanvas, Assert.IsAssignableFrom<ISolidColorBrush>(view.Background).Color);
        Assert.NotEqual(lightGrid, Assert.IsAssignableFrom<ISolidColorBrush>(plot.GridBrush).Color);
        Capture(window, "workspace-dark.png");

        // Emulate the resource notifications that Fluent emits for an OS accent change.
        // Headless has no operating system preference service to manipulate.
        app.Resources["SystemAccentColor"] = Color.Parse("#A84078");
        app.Resources["SystemAccentColorLight2"] = Color.Parse("#EC90C1");
        Dispatcher.UIThread.RunJobs();
        var primary = view.FindControl<Button>("PlaybackButton")!;
        var before = Assert.IsAssignableFrom<ISolidColorBrush>(primary.Background).Color;
        var chartBefore = Assert.IsAssignableFrom<ISolidColorBrush>(plot.ChannelABrush).Color;
        app.Resources["SystemAccentColor"] = Color.Parse("#227D69");
        app.Resources["SystemAccentColorLight2"] = Color.Parse("#6BDAB5");
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(before, Assert.IsAssignableFrom<ISolidColorBrush>(primary.Background).Color);
        Assert.NotEqual(chartBefore, Assert.IsAssignableFrom<ISolidColorBrush>(plot.ChannelABrush).Color);
        Assert.NotEqual(default, Assert.IsAssignableFrom<ISolidColorBrush>(primary.Foreground).Color);
        Capture(window, "workspace-custom-accent.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task WorkspaceButtonsSlidersStopAndNavigationWork()
    {
        using var folder = new TestFolder();
        var transport = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1450, Height = 1100 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        Assert.Equal(10, vm.SessionMinutes);
        Dispatcher.UIThread.RunJobs();
        var connect = view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "连接");
        Click(window, connect);
        await WaitForAsync(() => vm.ConnectedCount == 1);
        var slider = view.FindControl<Slider>("ChannelASlider")!;
        slider.Value = 35;
        view.FindControl<Slider>("ChannelBSlider")!.Value = 20;
        Assert.Equal(35, vm.ChannelA);
        Click(window, view.FindControl<Button>("PlaybackButton")!);
        await WaitForAsync(() => vm.IsRunning);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await vm.TickAsync();
        Assert.True(transport.Outputs["sim-dual-01"].ChannelA > 0);
        Capture(window, "workspace.png");

        Click(window, view.FindControl<Button>("StopAllButton")!);
        await WaitForAsync(() => !vm.IsRunning);
        Assert.All(transport.Outputs.Values, output => Assert.Equal(0, output.ChannelA));
        Assert.Single(vm.Sessions);

        await vm.NavigateAsync("waves");
        vm.EditorName = "夜间节奏";
        vm.EditorShape = WaveShape.Custom;
        vm.EditorPoints = "0, 0.4, 1, 0.5, 0";
        Click(window, view.FindControl<Button>("SaveWaveButton")!);
        await WaitForAsync(() => vm.Presets.Count == 5);
        Assert.Equal("夜间节奏", vm.SelectedPreset!.Name);
        Capture(window, "waveforms.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task CompactLayoutAndPersistenceWorkflowWork()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 760, Height = 1040 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindControl<Border>("Sidebar")!.IsVisible);
        Assert.True(view.FindControl<StackPanel>("CompactNav")!.IsVisible);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        await vm.ToggleConnectionAsync(vm.Devices[1]);
        vm.LinkMode = true; vm.ChannelA = 25; vm.ChannelB = 15;
        await vm.TogglePlaybackAsync();
        await vm.NavigateAsync("history");
        Assert.False(vm.IsRunning);
        Assert.Equal(2, Assert.Single(vm.Sessions).Record.DeviceCount);
        Capture(window, "compact-history.png");
        await vm.NavigateAsync("waves");
        await vm.ImportAsync(WorkspaceStore.ExportPresets([WaveformPreset.BuiltIn[0] with { Id = Guid.NewGuid(), Name = "导入测试" }]));
        await vm.ImportAsync(WorkspaceStore.ExportPresets(vm.Presets.Select(p => p.Preset)));
        Assert.Equal(5, vm.Presets.Count);
        await vm.SaveAsync();
        var loaded = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Single(loaded.Presets);
        Assert.Single(loaded.Sessions);
        window.Close();
    }

    private static void Click(Window window, Button button)
    {
        Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "UI operation did not complete within 5 seconds.");
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
}
