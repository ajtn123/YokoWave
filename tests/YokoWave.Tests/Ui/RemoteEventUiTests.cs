using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views.Pipelines;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Remote;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class RemoteEventUiTests
{
    [AvaloniaFact]
    public async Task EventCatalogFollowsExposedSubgraphPortsAndCopiedConnectionsRemainIndependent()
    {
        using var state = new UiTestState();
        await using var controller = new DeviceController(new SimulationTransport());
        var inner = new PipelineBuilder("Endpoint");
        var server = inner.Add(RemoteEventNodes.Server, new EventServerNode(new(Port: 0)));
        var root = new PipelineBuilder("Nested events");
        var wrapper = root.Add(SubgraphExpansion.Type, new SubgraphNode(inner.Build(), [],
            [new("events", "Pipeline.Port.Events", new(server.Id, "events"), Kind: PortValueKind.Event)]));
        var input = root.Add(RemoteEventNodes.Input, new EventInputNode());
        root.Connect(wrapper, "events", input, "events");
        List<Exception> errors = [];
        await using var editor = new PipelineEditorViewModel(root.Build(), controller, [], errors.Add, _ => { }, registry: RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry()));
        var selection = Assert.IsType<EventInputEditorViewModel>(editor.Nodes.Single(node => node.Id == input.Id));
        await editor.StartAsync();
        var endpoint = Assert.Single(editor.Services.Endpoints);
        await using var client = new RemoteEventClient(endpoint.Value.Url, editor.Services.Credentials[endpoint.Key].Token,
            new("game", [new("collision", RemoteEventKind.Trigger)]));
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await UiActions.EventuallyAsync(() => selection.Events.Count == 1);
        selection.SelectedEvent = Assert.Single(selection.Events);
        await editor.StopAsync();
        foreach (var node in editor.Nodes)
            editor.SelectedNodes.Add(node);
        editor.CopyCommand.Execute(null);
        editor.PasteCommand.Execute(null);
        var copied = Assert.IsType<EventInputEditorViewModel>(editor.Nodes.Single(node => node.Type == RemoteEventNodes.Input && node.Id != input.Id));
        Assert.Equal("game", copied.ClientId);
        Assert.Empty(copied.Events);
        Assert.Equal(2, editor.Connections.Count);
        Assert.Single(editor.Connections, connection => connection.Target.Node.Id == copied.Id && connection.Source.Node.Id != wrapper.Id);
        Assert.Empty(editor.Diagnostics);
        await editor.StartAsync();
        Assert.Equal(2, editor.Services.Endpoints.Count);
        await editor.StopAsync();
        Assert.Empty(errors);
    }

    [AvaloniaFact]
    public async Task EventFormsDiscoverDeclarationsSaveInPlaceAndRestoreAsReusableTemplates()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var controller = new DeviceController(new SimulationTransport());
        List<Exception> errors = [];
        Guid graphId;
        Guid inputId;
        string token;
        await using (var workspace = new PipelinesWorkspaceViewModel(controller, folder.Path, [], errors.Add, _ => { }, () => 7200, () => .6))
        {
            await workspace.LoadAsync();
            workspace.AddDocument(RemoteEventNodes.Template.Create(new("Events")));
            var editor = workspace.Selected!;
            graphId = editor.Id;
            var server = Assert.IsType<ServerNodeEditorViewModel>(editor.Nodes.Single(node => node.Type == RemoteEventNodes.Server));
            var input = Assert.IsType<EventInputEditorViewModel>(editor.Nodes.Single(node => node.Type == RemoteEventNodes.Input));
            inputId = input.Id;
            server.Port = 0;
            token = server.Token;
            Assert.False(server.HasControlLimit);
            Assert.Empty(editor.ReferencedDeviceIds);
            await editor.StartAsync();
            await using var client = new RemoteEventClient(Assert.Single(editor.Services.Endpoints).Value.Url, token,
                new("game", [new("collision", RemoteEventKind.Trigger, "Collision"), new("score", RemoteEventKind.Number, "Score", 0, 100)]));
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            await UiActions.EventuallyAsync(() => input.Events.Count == 2);
            input.SelectedEvent = input.Events.Single(option => option.Descriptor.Event.Id == "score");
            Assert.Equal("game", input.ClientId);
            Assert.Equal("score", input.EventId);
            Assert.Same(input, editor.Nodes.Single(node => node.Id == inputId));
            using var sink = new BindingLogCapture();
            var inspector = new NodeInspectorView();
            var window = new Window { Width = 550, Height = 850, Content = new ScrollViewer { Content = inspector } };
            try
            {
                window.Show();
                foreach (string language in new[] { "en", "zh-CN" })
                {
                    L10n.Current.SetLanguage(language);
                    editor.RefreshLocalization();
                    foreach (var node in new NodeEditorViewModel[] { server, input })
                    {
                        inspector.DataContext = node;
                        Dispatcher.UIThread.RunJobs();
                        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.StartsWith('[') == true);
                    }
                    Assert.NotNull(input.SelectedEvent);
                    UiArtifacts.Capture(window, $"event-input-{language}.png");
                }
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window.Close();
            }
            await editor.StopAsync();
            Assert.Equal(2, input.Events.Count);
            workspace.TemplateName = "Event wrapper";
            await workspace.SaveTemplateCommand.ExecuteAsync();
            workspace.Add();
            var wrapper = workspace.Selected!;
            Assert.Equal(SubgraphExpansion.Type, Assert.Single(wrapper.Nodes).Type);
            await wrapper.StartAsync();
            Assert.Single(wrapper.Services.Endpoints);
            await wrapper.StopAsync();
            await workspace.SaveAsync();
        }
        await using var restored = new PipelinesWorkspaceViewModel(controller, folder.Path, [], errors.Add, _ => { }, () => 7200, () => .6);
        await restored.LoadAsync();
        var loadedGraph = restored.Pipelines.Single(graph => graph.Id == graphId);
        var loaded = Assert.IsType<EventInputEditorViewModel>(loadedGraph.Nodes.Single(node => node.Id == inputId));
        Assert.Equal("game", loaded.ClientId);
        Assert.Equal("score", loaded.EventId);
        Assert.Empty(loaded.Events);
        Assert.Equal(0, Assert.IsType<ServerNodeEditorViewModel>(loadedGraph.Nodes.Single(node => node.Type == RemoteEventNodes.Server)).Port);
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(Path.Combine(folder.Path, "pipelines.json"), TestContext.Current.CancellationToken));
        Assert.Empty(errors);
    }
}
