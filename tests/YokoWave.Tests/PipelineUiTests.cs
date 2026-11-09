using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views.Pipelines;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class PipelineUiTests
{
    [AvaloniaFact]
    public async Task EditorConnectsSelectsCopiesUndoesPersistsAndRunsNumericParameters()
    {
        using var folder = new TestFolder();
        await using var controller = new DeviceController(new SimulationTransport());
        ObservableCollection<PresetItem> presets = new(WaveformPreset.BuiltIn.Select(p => new PresetItem(p)));
        List<Exception> errors = [];
        await using var workspace = new PipelinesWorkspaceViewModel(controller, folder.Path, presets, errors.Add, _ => { }, () => 7200, () => .6);
        await workspace.LoadAsync();
        var valueNode = NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(.3));
        var sinkNode = NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode("test"));
        workspace.AddDocument(new(Guid.NewGuid(), "test", [valueNode, sinkNode], [new(new(valueNode.Id, "value"), new(sinkNode.Id, "strength"))], [new(valueNode.Id, 0, 0), new(sinkNode.Id, 350, 0)]));
        var editor = workspace.Selected!;
        editor.ShowPalette = true;
        var device = new DeviceDescriptor("test", "Test device", DeviceKind.DualChannel, 90, -30);
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        editor.RefreshDevices();
        var output = editor.Nodes.OfType<ChannelOutputEditorViewModel>().Single();
        output.SelectedDevice = editor.Devices.Single(d => d.Id == device.Id);
        var constant = editor.Nodes.OfType<ConstantEditorViewModel>().Single();
        constant.Value = .3;
        editor.TimerEnabled = true;
        editor.DurationSeconds = 3;
        Assert.Empty(editor.Diagnostics);
        var window = new Window { Width = 1500, Height = 850, Content = new PipelinesWorkspaceView { DataContext = workspace } };
        var previousLogger = Logger.Sink;
        var sink = new BindingSink();
        Logger.Sink = sink;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var canvas = window.GetVisualDescendants().OfType<NodifyEditor>().Single();
            canvas.FitToScreen();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, canvas.ItemsSource!.Cast<object>().Count());
            var item = window.GetVisualDescendants().OfType<ItemContainer>().Single(c => ReferenceEquals(c.DataContext, constant));
            var point = item.TranslatePoint(new Point(20, 10), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(constant, editor.SelectedNode);
            var input = window.GetVisualDescendants().OfType<NumericUpDown>().Single(c => c.Name == "ConstantValueInput");
            input.Value = .4m;
            Assert.Equal(.4, constant.Value);
            var position = constant.Location;
            constant.Location = position + new Vector(30, 30);
            editor.UndoCommand.Execute(null);
            Assert.Equal(position, editor.Nodes.Single(n => n.Id == constant.Id).Location);
            editor.RedoCommand.Execute(null);
            constant = editor.Nodes.OfType<ConstantEditorViewModel>().Single();
            editor.SelectedNodes.Add(constant);
            editor.CopyCommand.Execute(null);
            var count = editor.Nodes.Count;
            editor.PasteCommand.Execute(null);
            Assert.Equal(count + 1, editor.Nodes.Count);
            editor.UndoCommand.Execute(null);
            Assert.Equal(count, editor.Nodes.Count);
            var extra = editor.AddNode(BuiltInNodes.Constant);
            var frequency = editor.Nodes.OfType<ChannelOutputEditorViewModel>().Single().Inputs.Single(p => p.Port.Id == "frequency");
            Assert.True(editor.Connect(extra.Outputs.Single(), frequency));
            Assert.False(editor.Connect(extra.Outputs.Single(), frequency));
            ((ConstantEditorViewModel)extra).Value = 1;
            var capPort = editor.Nodes.OfType<ChannelOutputEditorViewModel>().Single().Inputs.Single(p => p.Port.Id == "limit");
            Dispatcher.UIThread.RunJobs();
            canvas.FitToScreen();
            Dispatcher.UIThread.RunJobs();
            var sourceConnector = window.GetVisualDescendants().OfType<Connector>().Single(c => ReferenceEquals(c.DataContext, extra.Outputs.Single()));
            var targetConnector = window.GetVisualDescendants().OfType<Connector>().Single(c => ReferenceEquals(c.DataContext, capPort));
            static Point ThumbPoint(Connector connector, Window root)
            {
                var thumb = connector.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Connector");
                return thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), root)!.Value;
            }
            var sourcePoint = ThumbPoint(sourceConnector, window);
            var targetPoint = ThumbPoint(targetConnector, window);
            window.MouseMove(sourcePoint);
            window.MouseDown(sourcePoint, MouseButton.Left);
            window.MouseMove(sourcePoint + new Vector(12, 0));
            window.MouseMove(targetPoint);
            window.MouseUp(targetPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(editor.Connections, c => c.Source.Node == extra && c.Target == capPort);
            var beforeLanguage = editor.ToDocument();
            L10n.Current.SetLanguage("zh-CN");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(JsonSerializer.Serialize(beforeLanguage, PipelineJson.Options), JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options));
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            canvas = window.GetVisualDescendants().OfType<NodifyEditor>().Single();
            canvas.FitToScreen();
            Capture(window, "pipeline-dark.png");
            window.RequestedThemeVariant = ThemeVariant.Light;
            Capture(window, "pipeline-light.png");
            await editor.StartAsync();
            Assert.False(editor.CanEdit);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.GetVisualDescendants().OfType<ProgressBar>().Single(c => c.Name == "PipelineTimerProgress").IsVisible);
            Assert.False(window.GetVisualDescendants().OfType<Slider>().Single(c => c.Name == "PipelineDurationSlider").IsVisible);
            var runningNodes = editor.Nodes.Count;
            editor.AddNode(BuiltInNodes.Constant);
            Assert.Equal(runningNodes, editor.Nodes.Count);
            constant = editor.Nodes.OfType<ConstantEditorViewModel>().First(c => c.Id == constant.Id);
            constant.Value = .2;
            await Task.Delay(150, TestContext.Current.CancellationToken);
            await editor.Session.StopAsync();
            Assert.True(editor.CanEdit);
            Assert.Equal("00:03", editor.TimerReadout);
            editor.TimerEnabled = false;
            await workspace.SaveAsync();
            var saved = (await new PipelineStore(folder.Path).LoadAsync()).Single(p => p.Id == editor.Id);
            Assert.False(saved.TimerEnabled);
            Assert.Equal(3, saved.DurationSeconds);
            Assert.Empty(sink.Errors);
            Assert.Empty(errors);
        }
        finally
        {
            window.Close();
            L10n.Current.SetLanguage("en");
            Logger.Sink = previousLogger;
        }
    }

    [AvaloniaFact]
    public async Task NodeLibraryRenders200NodesAnd500EdgesAndRecordsMeasurements()
    {
        using var folder = new TestFolder();
        await using var controller = new DeviceController(new SimulationTransport());
        List<NodeDefinition> nodes = [NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(.5))];
        for (var i = 0; i < 139; i++)
            nodes.Add(NodeDefinition.Create(BuiltInNodes.Gain, new GainNode()));
        for (var i = 0; i < 60; i++)
            nodes.Add(NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode($"device-{i}")));
        var registry = BuiltInNodes.CreateRegistry();
        var targets = nodes.Skip(1).SelectMany(node => registry.Get(node.Type).Ports.Where(p => p.Direction == PortDirection.Input)
            .Select(port => new PortAddress(node.Id, port.Id))).Take(500).ToArray();
        var graph = new PipelineDocument(Guid.NewGuid(), "200 / 500", nodes.ToArray(),
            targets.Select(target => new PipelineConnection(new(nodes[0].Id, "value"), target)).ToArray(),
            nodes.Select((node, i) => new NodeLayout(node.Id, i % 20 * 240, i / 20 * 180)).ToArray());
        var allocated = GC.GetTotalAllocatedBytes();
        var elapsed = Stopwatch.StartNew();
        await using var editor = new PipelineEditorViewModel(graph, controller,
            new(WaveformPreset.BuiltIn.Select(p => new PresetItem(p))), _ => { }, _ => { });
        var createdMs = elapsed.Elapsed.TotalMilliseconds;
        var window = new Window { Width = 1200, Height = 800, Content = new PipelineEditorView { DataContext = editor } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<NodifyEditor>().Single().FitToScreen();
            Dispatcher.UIThread.RunJobs();
            Capture(window, "pipeline-200-nodes.png");
            var directory = ArtifactDirectory();
            await File.WriteAllTextAsync(Path.Combine(directory, "pipeline-editor-measurements.json"), JsonSerializer.Serialize(new
            {
                Nodes = 200,
                Edges = 500,
                CreatedMs = createdMs,
                RenderedMs = elapsed.Elapsed.TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                Environment = "Avalonia headless, Windows, Release; initial render only"
            }), TestContext.Current.CancellationToken);
        }
        finally { window.Close(); }
    }

    private static string ArtifactDirectory()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(ArtifactDirectory(), name), PngBitmapEncoderOptions.Default);
    }
    private sealed class BindingSink : ILogSink
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
}
