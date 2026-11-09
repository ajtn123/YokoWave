using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YokoWave.App.Controls;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class GraphWorkspaceUiTests
{
    [AvaloniaFact]
    public async Task CopiesStartUnboundAndIdleDevicesCannotBelongToTwoGraphs()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(folder.Path));
        await vm.InitializeAsync();
        var first = vm.Pipelines.Selected!;
        var device = vm.Devices.Devices.First(d => d.Device.Kind == DeviceKind.DualChannel);
        first.RebindDevice("$device", device.Id);
        vm.Pipelines.DuplicateCommand.Execute(null);
        var second = vm.Pipelines.Selected!;
        Assert.DoesNotContain(device.Id, second.BoundDeviceIds);
        var placeholder = Assert.Single(second.BoundDeviceIds);
        Assert.ThrowsAny<InvalidOperationException>(() => second.RebindDevice(placeholder, device.Id));
        Assert.DoesNotContain(device.Id, second.BoundDeviceIds);
        first.RebindDevice(device.Id, "$device");
        second.RebindDevice(placeholder, device.Id);
        Assert.Contains(device.Id, second.BoundDeviceIds);
        Assert.False(first.CanUndo);
        await vm.Pipelines.SaveAsync();
    }

    [AvaloniaFact]
    public async Task AllNodeInspectorsAndTemplatesRenderInBothLanguagesWithoutBindingErrors()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(folder.Path));
        await vm.InitializeAsync();
        var previous = Logger.Sink;
        var language = L10n.Current.Language;
        var sink = new BindingSink();
        Logger.Sink = sink;
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 1700, Height = 950, Content = view };
        try
        {
            window.Show();
            var editor = vm.Pipelines.Selected!;
            foreach (var registration in editor.Registry.Types)
            {
                var node = editor.Nodes.FirstOrDefault(n => n.Type == registration.Type) ?? editor.AddNode(registration.Type);
                editor.SelectedNodes.Clear();
                editor.SelectedNodes.Add(node);
                foreach (var locale in new[] { "en", "zh-CN" })
                {
                    L10n.Current.SetLanguage(locale);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Empty(window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Text?.StartsWith('[') == true).Select(text => text.Text));
                }
            }
            Assert.Empty(sink.Errors);
            foreach (var choice in vm.Pipelines.Templates.ToArray())
            {
                vm.Pipelines.SelectedTemplate = choice;
                vm.Pipelines.Add();
                Dispatcher.UIThread.RunJobs();
                Assert.NotEmpty(vm.Pipelines.Selected!.Nodes);
            }
            foreach (var page in new[] { "devices", "waves", "history", "settings", "help", "pipelines" })
            {
                await vm.NavigateAsync(page);
                Dispatcher.UIThread.RunJobs();
            }
            window.Width = 900;
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(sink.Errors);
        }
        finally
        {
            window.Close();
            Logger.Sink = previous;
            L10n.Current.SetLanguage(language);
        }
    }

    [AvaloniaFact]
    public async Task GraphOwnsTargetsRunsAcrossNavigationAndSavesReusableTemplatesWithoutRuntimeState()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(folder.Path));
        await vm.InitializeAsync();
        var device = vm.Devices.Devices.First(d => d.Device.Kind == DeviceKind.DualChannel);
        await vm.Devices.ToggleConnectionAsync(device);
        var editor = vm.Pipelines.Selected!;
        editor.RebindDevice("$device", device.Id);
        foreach (var constant in editor.Nodes.OfType<ConstantEditorViewModel>())
            constant.Value = .25;
        editor.TimerEnabled = false;
        var snapshot = JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options);
        await editor.StartAsync();
        await vm.NavigateAsync("help");
        Assert.Equal(PipelineState.Running, editor.Session.State);
        Assert.False(editor.CanEdit);
        var count = editor.Nodes.Count;
        editor.AddNode(BuiltInNodes.Constant);
        Assert.Equal(count, editor.Nodes.Count);
        editor.OutputLimit = .1;
        await editor.Session.FlushAsync();
        Assert.All(editor.Session.LastFrame!.Outputs.Values, frame => Assert.InRange(frame.ChannelA, 0, 10));
        await editor.StopAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.History.Sessions);
        vm.Pipelines.TemplateName = "my graph";
        vm.Pipelines.SaveTemplateCommand.Execute(null);
        await Wait(() => vm.Pipelines.SelectedTemplate?.IsBuiltIn == false);
        vm.Pipelines.Add();
        Assert.Equal(SubgraphExpansion.Type, Assert.Single(vm.Pipelines.Selected!.Nodes).Type);
        await vm.Pipelines.SaveAsync();
        await vm.SaveAsync();
        var saved = await File.ReadAllTextAsync(Path.Combine(folder.Path, "pipelines.json"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("PipelineSession", saved);
        Assert.DoesNotContain("Token", saved);
        Assert.DoesNotContain("\n", saved);
        Assert.False(File.Exists(Path.Combine(folder.Path, "modes.json")));
        Assert.NotEmpty(snapshot);
        Assert.Single(await new PipelineStore(folder.Path).LoadPresetsAsync());
    }

    [AvaloniaFact]
    public async Task NativeAndHardwareDraftsKeepStateAndThemeAndAccentRemainDynamic()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(folder.Path));
        await vm.InitializeAsync();
        vm.Pipelines.SelectedTemplate = vm.Pipelines.Templates.Single(t => t.Name == L10n.Get("Template.NativeProgram"));
        vm.Pipelines.Add();
        var editor = vm.Pipelines.Selected!;
        var native = editor.Nodes.OfType<NativeOutputEditorViewModel>().Single();
        native.NativeEditor.PresetA = 7;
        native.Options.FrequencyA = 100;
        var before = JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options);
        L10n.Current.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options));
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 1500, Height = 850, Content = view, RequestedThemeVariant = ThemeVariant.Light };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var light = Assert.IsAssignableFrom<ISolidColorBrush>(view.Background).Color;
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(light, Assert.IsAssignableFrom<ISolidColorBrush>(view.Background).Color);
            await vm.NavigateAsync("waves");
            Dispatcher.UIThread.RunJobs();
            var plot = window.GetVisualDescendants().OfType<WaveformPlot>().First();
            var app = Application.Current!;
            app.Resources["SystemAccentColorLight2"] = Color.Parse("#EC90C1");
            Dispatcher.UIThread.RunJobs();
            var accent = Assert.IsAssignableFrom<ISolidColorBrush>(plot.ChannelABrush).Color;
            app.Resources["SystemAccentColorLight2"] = Color.Parse("#6BDAB5");
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(accent, Assert.IsAssignableFrom<ISolidColorBrush>(plot.ChannelABrush).Color);
        }
        finally { window.Close(); }
    }

    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
    private sealed class BindingSink : ILogSink
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string template)
        {
            if (IsEnabled(level, area))
                Errors.Enqueue(template);
        }
        public void Log(LogEventLevel level, string area, object? source, string template, params object?[] values) => Log(level, area, source, template + string.Join(",", values));
    }
}
