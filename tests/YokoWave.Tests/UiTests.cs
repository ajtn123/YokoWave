using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Logging;
using System.Collections.Concurrent;
using YokoWave.App.Views.Pages;
using YokoWave.App.Views.Components;
using YokoWave.App.ViewModels;
using YokoWave.App.Views;
using YokoWave.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(YokoWave.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace YokoWave.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseHarfBuzz()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{

    [AvaloniaFact]
    public async Task SplitViewsProtectDefaultsAndPreserveSamplesWhenDuplicating()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 600 };
        window.Show();
        await WaitForAsync(() => vm.Waveforms.Presets.Count == DefaultWaveformCatalog.Presets.Count && !vm.Devices.IsScanning);
        Assert.NotNull(Find<DeviceWorkspaceView>(view, "WorkspacePage"));
        Assert.NotNull(Find<WaveformLibraryView>(view, "WavesPage"));
        Assert.NotNull(Find<SessionHistoryView>(view, "HistoryPage"));
        Assert.NotNull(Find<SettingsView>(view, "SettingsPage"));
        Assert.Single(view.GetLogicalDescendants().OfType<WaveformEditorView>());
        Assert.Single(view.GetLogicalDescendants().OfType<WaveformTransferView>());
        await vm.NavigateAsync("waves");
        var original = vm.Waveforms.LibrarySelectedPreset!.Preset;
        Assert.False(vm.Waveforms.DeleteWaveCommand.CanExecute(null));
        Assert.False(Find<Button>(view, "DeleteWaveButton").IsEffectivelyEnabled);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(vm.Waveforms.DeleteWaveAsync);
        vm.Waveforms.EditWaveCommand.Execute(null);
        await vm.Waveforms.SaveWaveAsync();
        Assert.Equal(DefaultWaveformCatalog.Presets.Count + 1, vm.Waveforms.Presets.Count);
        Assert.False(vm.Waveforms.LibrarySelectedPreset!.IsBuiltIn);
        Assert.Equivalent(original.Samples, vm.Waveforms.LibrarySelectedPreset.Preset.Samples);
        Assert.True(vm.Waveforms.DeleteWaveCommand.CanExecute(null));
        Assert.True(Find<Button>(view, "DeleteWaveButton").IsEffectivelyEnabled);
        Click(window, Find<Button>(view, "DeleteWaveButton"));
        await WaitForAsync(() => vm.Waveforms.Presets.Count == DefaultWaveformCatalog.Presets.Count);
        Assert.False(vm.Waveforms.DeleteWaveCommand.CanExecute(null));
        Capture(window, "default-waveforms.png");
        window.Close();
    }

    private sealed class BindingErrorSink : ILogSink
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area))
                Errors.Enqueue(messageTemplate);
        }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Log(level, area, source, $"{messageTemplate} [{string.Join(", ", propertyValues)}]");
    }

    private static T Find<T>(Control root, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs();
        return root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    }

    private static void Click(Window window, Button button)
    {
        // Nested playlist scrolling handles its own bring request; reveal that viewport first.
        foreach (var viewport in button.GetVisualAncestors().OfType<ScrollViewer>().Reverse())
        {
            viewport.BringIntoView();
            Dispatcher.UIThread.RunJobs();
        }
        button.BringIntoView();
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
            if (condition())
                return;
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
