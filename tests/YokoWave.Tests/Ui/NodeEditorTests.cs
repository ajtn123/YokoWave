using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class NodeEditorTests
{
    [AvaloniaFact]
    public async Task ConnectionGesturesSupportUndoAndKeepRunningTopologyLocked()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var controller = new DeviceController(new SimulationTransport());
        await controller.ConnectAsync(new("test", "Test device", DeviceKind.DualChannel, 90, -30), TestContext.Current.CancellationToken);
        var builder = new PipelineBuilder("disconnect");
        var value = builder.Add(BuiltInNodes.Constant, new ConstantNode(.3));
        for (int channel = 0; channel < 2; channel++)
        {
            var output = builder.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("test", channel));
            builder.Connect(value, "value", output, "strength");
        }

        List<Exception> errors = [];
        await using var editor = new PipelineEditorViewModel(builder.Build(), controller, [], errors.Add, _ => { });
        editor.RefreshDevices();
        var view = new PipelineEditorView
        {
            DataContext = editor
        };
        var window = new Window
        {
            Width = 1200,
            Height = 750,
            Content = view
        };
        window.Show();
        try
        {
            void PrepareCanvas()
            {
                Dispatcher.UIThread.RunJobs();
                var canvas = view.GetVisualDescendants().OfType<NodifyEditor>().Single();
                canvas.FitToScreen();
                Dispatcher.UIThread.RunJobs();
            }

            Point PortPosition(NodePortViewModel port)
            {
                var connector = view.GetVisualDescendants().OfType<Connector>().Single(c => ReferenceEquals(c.DataContext, port));
                var thumb = connector.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "PART_Connector");
                return thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            }

            void AltClick(NodePortViewModel port)
            {
                PrepareCanvas();
                var point = PortPosition(port);
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.Alt);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.Alt);
                Dispatcher.UIThread.RunJobs();
            }

            void Drag(NodePortViewModel from, NodePortViewModel to)
            {
                PrepareCanvas();
                var start = PortPosition(from);
                var end = PortPosition(to);
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Vector(12, 0));
                window.MouseMove(end);
                window.MouseUp(end, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }

            NodePortViewModel Source() => editor.Nodes.OfType<ConstantEditorViewModel>().Single().Outputs.Single();
            NodePortViewModel Input(string port) => editor.Nodes.OfType<ChannelOutputEditorViewModel>().First().Inputs.Single(p => p.Port.Id == port);
            Drag(Source(), Input("strength"));
            Assert.Single(editor.Connections);
            Assert.Equal(1, Assert.IsType<ChannelOutputEditorViewModel>(editor.Connections[0].Target.Node).Channel);
            editor.UndoCommand.Execute(null);
            Assert.Equal(2, editor.Connections.Count);
            editor.RedoCommand.Execute(null);
            Assert.Single(editor.Connections);
            editor.UndoCommand.Execute(null);
            Drag(Input("strength"), Source());
            Assert.Single(editor.Connections);
            editor.UndoCommand.Execute(null);
            for (int repetition = 0; repetition < 3; repetition++)
            {
                Drag(Source(), Input("limit"));
                Assert.Equal(3, editor.Connections.Count);
                Drag(Source(), Input("limit"));
                Assert.Equal(2, editor.Connections.Count);
            }

            var target = editor.Nodes.OfType<ChannelOutputEditorViewModel>().First().Inputs.Single(p => p.Port.Id == "strength");
            AltClick(target);
            Assert.Single(editor.Connections);
            Assert.Equal(3, editor.Nodes.Count);
            editor.UndoCommand.Execute(null);
            Assert.Equal(2, editor.Connections.Count);
            Dispatcher.UIThread.RunJobs();
            var line = view.GetVisualDescendants().OfType<Connection>().First();
            var middle = line.TranslatePoint(new Point((line.Source.X + line.Target.X) / 2, (line.Source.Y + line.Target.Y) / 2), window)!.Value;
            window.MouseMove(middle);
            window.MouseDown(middle, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseUp(middle, MouseButton.Left, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(editor.Connections);
            editor.UndoCommand.Execute(null);
            Assert.Equal(2, editor.Connections.Count);
            AltClick(editor.Nodes.OfType<ConstantEditorViewModel>().Single().Outputs.Single());
            Assert.Empty(editor.Connections);
            Assert.Equal(3, editor.Nodes.Count);
            editor.UndoCommand.Execute(null);
            Assert.Equal(2, editor.Connections.Count);
            await editor.StartAsync();
            Drag(Source(), Input("strength"));
            Assert.Equal(2, editor.Connections.Count);
            AltClick(editor.Nodes.OfType<ConstantEditorViewModel>().Single().Outputs.Single());
            Assert.Equal(2, editor.Connections.Count);
            await editor.StopAsync();
            Assert.Empty(errors);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EditorConnectsSelectsCopiesUndoesPersistsAndRunsNumericParameters()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var controller = new DeviceController(new SimulationTransport());
        ObservableCollection<WaveformItem> waveforms = new(WaveformAsset.BuiltIn.Select(p => new WaveformItem(p)));
        List<Exception> errors = [];
        await using var workspace = new PipelinesWorkspaceViewModel(controller, folder.Path, waveforms, errors.Add, _ => { }, () => 7200, () => .6);
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
        var window = new Window
        {
            Width = 1500,
            Height = 850,
            Content = new PipelinesWorkspaceView
            {
                DataContext = workspace
            }
        };
        using var sink = new BindingLogCapture();
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
            int count = editor.Nodes.Count;
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
            UiArtifacts.Capture(window, "pipeline-dark.png");
            window.RequestedThemeVariant = ThemeVariant.Light;
            UiArtifacts.Capture(window, "pipeline-light.png");
            await editor.StartAsync();
            Assert.False(editor.CanEdit);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.GetVisualDescendants().OfType<ProgressBar>().Single(c => c.Name == "PipelineTimerProgress").IsVisible);
            Assert.False(window.GetVisualDescendants().OfType<Slider>().Single(c => c.Name == "PipelineDurationSlider").IsVisible);
            int runningNodes = editor.Nodes.Count;
            editor.AddNode(BuiltInNodes.Constant);
            Assert.Equal(runningNodes, editor.Nodes.Count);
            constant = editor.Nodes.OfType<ConstantEditorViewModel>().First(c => c.Id == constant.Id);
            constant.Value = .2;
            await editor.Session.FlushAsync();
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
        }
    }

    [AvaloniaFact]
    public async Task LargeGraphsRenderAllNodesAndConnectionsWithoutBindingErrors()
    {
        using var state = new UiTestState();
        await using var controller = new DeviceController(new SimulationTransport());
        List<NodeDefinition> nodes = [NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode(.5))];
        for (int i = 0; i < 139; i++)
            nodes.Add(NodeDefinition.Create(BuiltInNodes.Gain, new GainNode()));
        for (int i = 0; i < 60; i++)
            nodes.Add(NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode($"device-{i}")));
        var registry = BuiltInNodes.CreateRegistry();
        var targets = nodes.Skip(1).SelectMany(node => registry.Get(node.Type).Ports.Where(p => p.Direction == PortDirection.Input).Select(port => new PortAddress(node.Id, port.Id))).Take(500).ToArray();
        var graph = new PipelineDocument(Guid.NewGuid(), "200 / 500", nodes.ToArray(), targets.Select(target => new PipelineConnection(new(nodes[0].Id, "value"), target)).ToArray(), nodes.Select((node, i) => new NodeLayout(node.Id, i % 20 * 240, i / 20 * 180)).ToArray());
        await using var editor = new PipelineEditorViewModel(graph, controller, new(WaveformAsset.BuiltIn.Select(p => new WaveformItem(p))), _ => { }, _ => { });
        using var bindings = new BindingLogCapture();
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = new PipelineEditorView
            {
                DataContext = editor
            }
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var canvas = window.GetVisualDescendants().OfType<NodifyEditor>().Single();
            canvas.FitToScreen();
            UiArtifacts.Capture(window, "pipeline-200-nodes.png");
            Assert.Equal(200, canvas.ItemsSource!.Cast<object>().Count());
            Assert.Equal(500, canvas.Connections!.Cast<object>().Count());
            Assert.Equal(200, canvas.GetVisualDescendants().OfType<ItemContainer>().Count());
            Assert.Equal(500, canvas.GetVisualDescendants().OfType<Connection>().Count());
            Assert.Empty(bindings.Errors);
        }
        finally
        {
            window.Close();
        }
    }
}
