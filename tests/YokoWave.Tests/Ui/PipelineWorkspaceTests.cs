using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class PipelineWorkspaceTests
{
    [AvaloniaFact]
    public async Task TemplatePortsSupportUndoSaveInstantiateExpandAndReload()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var controller = new DeviceController(new SimulationTransport());
        await controller.ConnectAsync(new("out", "out", DeviceKind.DualChannel, 80, -40), TestContext.Current.CancellationToken);
        List<Exception> errors = [];
        Guid copyId;
        {
            await using var workspace = new PipelinesWorkspaceViewModel(controller, folder.Path, new(WaveformAsset.BuiltIn.Select(w => new WaveformItem(w))), errors.Add, _ => { }, () => 7200, () => .6);
            await workspace.LoadAsync();
            var builder = new PipelineBuilder(new string('t', PipelineDocument.MaximumNameLength));
            var value = builder.Add(BuiltInNodes.Constant, new ConstantNode(.4));
            var output = builder.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out"));
            builder.Connect(value, "value", output, "strength");
            workspace.AddDocument(builder.Build());
            var editor = workspace.Selected!;
            NodePortViewModel Input() => editor.Nodes.OfType<ChannelOutputEditorViewModel>().Single().Inputs.Single(p => p.Port.Id == "strength");
            Input().IsExposed = true;
            Assert.Single(editor.ToDocument().Inputs!);
            editor.UndoCommand.Execute(null);
            Assert.Empty(editor.ToDocument().Inputs!);
            editor.RedoCommand.Execute(null);
            Assert.Single(editor.ToDocument().Inputs!);
            editor.Nodes.OfType<ConstantEditorViewModel>().Single().Outputs.Single().IsExposed = true;
            await editor.StartAsync();
            Input().IsExposed = false;
            Assert.True(Input().IsExposed);
            await editor.StopAsync();
            workspace.DuplicateCommand.Execute(null);
            Assert.Equal(PipelineDocument.MaximumNameLength, workspace.Selected!.Name.Length);
            Assert.EndsWith(L10n.Format("Pipeline.CopyName", ""), workspace.Selected.Name);
            workspace.Selected = editor;
            workspace.TemplateName = new string('w', PipelineDocument.MaximumNameLength);
            await workspace.SaveTemplateCommand.ExecuteAsync();
            workspace.Add();
            var copy = workspace.Selected!;
            Assert.Equal(PipelineDocument.MaximumNameLength, copy.Name.Length);
            Assert.EndsWith(workspace.Pipelines.Count.ToString(L10n.Current.Culture), copy.Name);
            copyId = copy.Id;
            var wrapper = Assert.Single(copy.Nodes);
            Assert.Empty(copy.Diagnostics);
            Assert.DoesNotContain("out", copy.AssignedDeviceIds);
            Assert.Single(wrapper.Inputs).IsExposed = true;
            Assert.Single(wrapper.Outputs).IsExposed = true;
            copy.ExpandSubgraphs();
            Assert.Empty(copy.Diagnostics);
            Assert.Single(copy.ToDocument().Inputs!);
            Assert.Single(copy.ToDocument().Outputs!);
            await workspace.SaveAsync();
        }

        await using var reopened = new PipelinesWorkspaceViewModel(controller, folder.Path, new(WaveformAsset.BuiltIn.Select(w => new WaveformItem(w))), errors.Add, _ => { }, () => 7200, () => .6);
        await reopened.LoadAsync();
        var restored = reopened.Pipelines.Single(p => p.Id == copyId);
        Assert.Single(restored.ToDocument().Inputs!);
        Assert.Single(restored.ToDocument().Outputs!);
        var plan = new PipelineCompiler(restored.Registry).Compile(restored.ToDocument(), []);
        var frame = Assert.Single(new PipelineRuntime(plan, new HashSet<string>()).Evaluate(TimeSpan.Zero, DateTimeOffset.UtcNow).Outputs).Value;
        Assert.Equal(40, frame.ChannelA);
        Assert.Empty(errors);
    }

    [AvaloniaFact]
    public async Task CopiesStartUnboundAndIdleDevicesCannotBelongToTwoGraphs()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), await folder.CreateSimulationStoreAsync());
        await vm.InitializeAsync();
        var first = vm.Pipelines.Selected!;
        var device = vm.Devices.Devices.First(d => d.Device.Kind == DeviceKind.DualChannel);
        first.RebindDevice("$device", device.Id);
        vm.Pipelines.DuplicateCommand.Execute(null);
        var second = vm.Pipelines.Selected!;
        Assert.DoesNotContain(device.Id, second.AssignedDeviceIds);
        string placeholder = Assert.Single(second.ReferencedDeviceIds);
        Assert.ThrowsAny<InvalidOperationException>(() => second.RebindDevice(placeholder, device.Id));
        Assert.DoesNotContain(device.Id, second.AssignedDeviceIds);
        first.RebindDevice(device.Id, "$device");
        second.RebindDevice(placeholder, device.Id);
        Assert.Contains(device.Id, second.AssignedDeviceIds);
        Assert.False(first.CanUndo);
        await vm.Pipelines.SaveAsync();
    }

    [AvaloniaFact]
    public async Task AllNodeInspectorsAndTemplatesRenderInBothLanguagesWithoutBindingErrors()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), await folder.CreateSimulationStoreAsync());
        await vm.InitializeAsync();
        string language = L10n.Current.Language;
        using var sink = new BindingLogCapture();
        var view = new MainView
        {
            DataContext = vm
        };
        var window = new Window
        {
            Width = 1700,
            Height = 950,
            Content = view
        };
        try
        {
            window.Show();
            var editor = vm.Pipelines.Selected!;
            foreach (var registration in editor.Registry.Types)
            {
                var node = editor.Nodes.FirstOrDefault(n => n.Type == registration.Type) ?? editor.AddNode(registration.Type);
                editor.SelectedNodes.Clear();
                editor.SelectedNodes.Add(node);
                foreach (string locale in JsonLocalizer.SupportedLanguages)
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

            foreach (string? page in new[] { "devices", "waves", "history", "settings", "help", "pipelines" })
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
            L10n.Current.SetLanguage(language);
        }
    }

    [AvaloniaFact]
    public async Task GraphOwnsTargetsRunsAcrossNavigationAndSavesReusableTemplatesWithoutRuntimeState()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), await folder.CreateSimulationStoreAsync());
        await vm.InitializeAsync();
        var device = vm.Devices.Devices.First(d => d.Device.Kind == DeviceKind.DualChannel);
        await vm.Devices.ToggleConnectionAsync(device);
        var editor = vm.Pipelines.Selected!;
        editor.RebindDevice("$device", device.Id);
        foreach (var constant in editor.Nodes.OfType<ConstantEditorViewModel>())
            constant.Value = .25;
        editor.TimerEnabled = false;
        string snapshot = JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options);
        await editor.StartAsync();
        await vm.NavigateAsync("help");
        Assert.Equal(PipelineState.Running, editor.Session.State);
        Assert.False(editor.CanEdit);
        int count = editor.Nodes.Count;
        editor.AddNode(BuiltInNodes.Constant);
        Assert.Equal(count, editor.Nodes.Count);
        foreach (var output in editor.Nodes.Where(n => n.HasStrengthLimit))
            output.StrengthLimit = .1;
        await editor.Session.FlushAsync();
        Assert.All(editor.Session.LastFrame!.Outputs.Values, frame => Assert.InRange(frame.ChannelA, 0, 10));
        await editor.StopAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.History.Sessions);
        vm.Pipelines.TemplateName = "my graph";
        vm.Pipelines.SaveTemplateCommand.Execute(null);
        await UiActions.EventuallyAsync(() => vm.Pipelines.SelectedTemplate?.IsBuiltIn == false);
        vm.Pipelines.Add();
        var wrapper = Assert.Single(vm.Pipelines.Selected!.Nodes).ToDefinition();
        Assert.Equal(SubgraphExpansion.Type, wrapper.Type);
        var subgraph = (SubgraphNode)vm.Pipelines.Selected.Registry.Get(wrapper.Type).Read(wrapper.Configuration);
        Assert.Empty(subgraph.Graph.AssignedDevices!);
        await vm.Pipelines.SaveAsync();
        await vm.SaveAsync();
        string saved = await File.ReadAllTextAsync(Path.Combine(folder.Path, "pipelines.json"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("PipelineSession", saved);
        Assert.DoesNotContain("Token", saved);
        Assert.DoesNotContain("\n", saved);
        Assert.False(File.Exists(Path.Combine(folder.Path, "modes.json")));
        Assert.NotEmpty(snapshot);
        Assert.Single(await new PipelineStore(folder.Path).LoadTemplatesAsync());
    }

    [AvaloniaFact]
    public async Task NativeAndHardwareDraftsKeepStateAndThemeAndAccentRemainDynamic()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), await folder.CreateSimulationStoreAsync());
        await vm.InitializeAsync();
        vm.Pipelines.SelectedTemplate = vm.Pipelines.Templates.Single(t => t.Name == L10n.Get("Template.NativeProgram"));
        vm.Pipelines.Add();
        var editor = vm.Pipelines.Selected!;
        var native = editor.Nodes.OfType<NativeOutputEditorViewModel>().Single();
        native.NativeEditor.PresetA = 7;
        string before = JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options);
        L10n.Current.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options));
        var view = new MainView
        {
            DataContext = vm
        };
        var window = new Window
        {
            Width = 1500,
            Height = 850,
            Content = view,
            RequestedThemeVariant = ThemeVariant.Light
        };
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
        finally
        {
            window.Close();
        }
    }
}
