using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Remote.DgLab.V4;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class DgLabV4UiTests
{
    [AvaloniaFact]
    public async Task SourceShowsPairingWithoutTokenAndLanguageChangesRetainTheQrCode()
    {
        using var state = new UiTestState();
        await using var relay = new DgLabV4Relay(new(Port: 0));
        await relay.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var controller = new DeviceController(new SimulationTransport());
        List<Exception> errors = [];
        await using var sources = new RemoteSourcesViewModel(controller, () => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask, errors.Add)
        {
            Name = "V4",
            Url = relay.Url!.ToString(),
            AdvertisedHost = "192.168.1.25"
        };
        sources.Kind = sources.Kinds.Single(option => option.Value == (int)RemoteConnectionKind.DgLabV4);
        Assert.False(sources.NeedsToken);
        await sources.AddCommand.ExecuteAsync();
        var item = Assert.Single(sources.Sources);
        await item.ConnectCommand.ExecuteAsync();
        Assert.True(item.IsConnected);
        Assert.False(item.NeedsToken);
        Assert.True(item.PairingCode.HasCode);
        Assert.Contains("192.168.1.25", Uri.UnescapeDataString(item.PairingCode.Url));
        var bitmap = item.PairingCode.Image;
        using var sink = new BindingLogCapture();
        var window = new Window
        {
            Width = 620,
            Height = 1040,
            Content = new ScrollViewer { Content = new RemoteSourcesView { DataContext = sources } }
        };
        try
        {
            window.Show();
            L10n.Current.SetLanguage("en");
            sources.RefreshLocalization();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(L10n.Get("DgLab.Waiting"), item.Status);
            L10n.Current.SetLanguage("zh-CN");
            sources.RefreshLocalization();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(bitmap, item.PairingCode.Image);
            Assert.Empty(sink.Errors);
            UiArtifacts.Capture(window, "dglab-v4-pairing.png");
            await item.ConnectCommand.ExecuteAsync();
            Assert.False(item.PairingCode.HasCode);
            Assert.False(item.IsConnected);
            Assert.Empty(errors);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task V4NodeFormsSaveInPlaceAndRelayCanRunWithoutAnyDevice()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var controller = new DeviceController(new SimulationTransport());
        List<Exception> errors = [];
        Guid appId;
        Guid relayId;
        await using (var workspace = new PipelinesWorkspaceViewModel(controller, folder.Path, [], errors.Add, _ => { }, () => 7200, () => .6))
        {
            await workspace.LoadAsync();
            workspace.AddDocument(DgLabV4Nodes.RelayTemplate.Create(new("Relay")));
            var relay = workspace.Selected!;
            relayId = relay.Id;
            var settings = Assert.IsType<DgLabRelayEditorViewModel>(Assert.Single(relay.Nodes));
            settings.Port = 0;
            settings.Path = "/test";
            settings.HeartbeatMilliseconds = 200;
            settings.IdleMilliseconds = 2000;
            settings.CertificatePassword = "memory-only";
            Assert.Same(settings, Assert.Single(relay.Nodes));
            Assert.True(relay.CanStart);
            await relay.StartAsync();
            Assert.Single(relay.Services.Endpoints);
            await relay.StopAsync();
            workspace.TemplateName = "Relay wrapper";
            await workspace.SaveTemplateCommand.ExecuteAsync();
            workspace.Add();
            var wrapper = workspace.Selected!;
            Assert.Equal(SubgraphExpansion.Type, Assert.Single(wrapper.Nodes).Type);
            Assert.True(wrapper.CanStart);
            await wrapper.StartAsync();
            await wrapper.StopAsync();
            workspace.AddDocument(DgLabV4Nodes.AppTemplate.Create(new("App")));
            var app = workspace.Selected!;
            appId = app.Id;
            var source = Assert.IsType<DgLabAppEditorViewModel>(app.Nodes.Single(node => node.Type == DgLabV4Nodes.App));
            source.Url = "ws://localhost:9998/test";
            source.ControllerId = "controller";
            Assert.Same(source, app.Nodes.Single(node => node.Id == source.Id));
            await workspace.SaveAsync();
        }
        await using var restored = new PipelinesWorkspaceViewModel(controller, folder.Path, [], errors.Add, _ => { }, () => 7200, () => .6);
        await restored.LoadAsync();
        var loaded = Assert.IsType<DgLabRelayEditorViewModel>(Assert.Single(restored.Pipelines.Single(graph => graph.Id == relayId).Nodes));
        Assert.Equal("/test", loaded.Path);
        Assert.Equal(200, loaded.HeartbeatMilliseconds);
        Assert.Equal(2000, loaded.IdleMilliseconds);
        Assert.Empty(loaded.CertificatePassword);
        var input = Assert.IsType<DgLabAppEditorViewModel>(restored.Pipelines.Single(graph => graph.Id == appId).Nodes.Single(node => node.Type == DgLabV4Nodes.App));
        Assert.Equal("ws://localhost:9998/test", input.Url);
        Assert.Equal("controller", input.ControllerId);
        Assert.Empty(errors);
    }
}
