using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YokoWave.App.ViewModels;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using Xunit;

namespace YokoWave.Tests;

public sealed class RemoteUiTests
{
    [AvaloniaFact]
    public async Task InputDevicesCanBeAssignedToServerModesButNotOutputModes()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        await store.SaveAsync(new(WorkspaceData.CurrentSchema, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), store, () => new BluetoothDeviceTransport(new HardwareGattAdapter(BluetoothProtocol.Civet)));
        await vm.InitializeAsync();
        await vm.ScanAsync();
        var sensor = Assert.Single(vm.Devices);
        Assert.False(sensor.CanAssign);
        await vm.AddModeAsync(ModeKind.WebSocketServer);
        Assert.True(sensor.CanAssign);
        sensor.AssignCommand.Execute(null);
        Assert.Contains(sensor.Id, vm.SelectedMode.TargetIds);
        Assert.False(sensor.CanAssign);
        Assert.Single(vm.SelectedMode.Targets);
    }

    [AvaloniaFact]
    public async Task SavingMixedSourcesPreservesTheSelectedLocalBackendAndConnections()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        var controller = new DeviceController(new SimulationTransport());
        await using var vm = new MainViewModel(controller, store);
        await vm.InitializeAsync();
        await controller.AddSourceAsync("additional", new BluetoothDeviceTransport(new HardwareGattAdapter(BluetoothProtocol.CoyoteV3)), TestContext.Current.CancellationToken);
        await vm.ScanAsync();
        var real = vm.Devices.Single(device => device.Device.ProtocolId == BluetoothProtocol.CoyoteV3.ToString());
        await vm.ToggleConnectionAsync(real);
        Assert.False(vm.UsesLocalBluetooth);
        await vm.SaveSettingsAsync();
        Assert.True(real.IsConnected);
        Assert.False((await store.LoadAsync()).Settings.UseBluetooth);
        Assert.False(controller.AreSimulated([real.Id]));
    }

    [AvaloniaFact]
    public async Task ServerAndSourceEditorsPersistSettingsWithoutSecretsOrAutomaticConnections()
    {
        using var folder = new TestFolder();
        var store = new WorkspaceStore(folder.Path);
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), store);
        await vm.InitializeAsync();
        await vm.AddModeAsync(ModeKind.WebSocketServer);
        var mode = vm.SelectedMode;
        var server = mode.ServerEditor;
        server.Port = 9012;
        server.ControlLimit = 35;
        server.CertificatePath = "certificate.pfx";
        server.CertificatePassword = "test-only-certificate-secret";
        var sources = vm.RemoteSources;
        sources.Name = "Test endpoint";
        sources.Url = "ws://127.0.0.1:9012/ws/123";
        sources.Token = "test-only-access-secret";
        var serverWindow = new Window { Content = new RemoteServerModeView { DataContext = mode } };
        var sourcesWindow = new Window { Content = new RemoteSourcesView { DataContext = sources } };
        var language = L10n.Current.Language;
        try
        {
            serverWindow.Show();
            sourcesWindow.Show();
            sources.AddCommand.Execute(null);
            for (var attempt = 0; attempt < 100 && sources.Sources.Count == 0; attempt++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Single(sources.Sources);
            vm.SelectedLanguage = vm.Languages.Single(item => item.Code == (language == "en" ? "zh-CN" : "en"));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(9012, server.Port);
            Assert.Equal("test-only-access-secret", sources.Sources[0].Token);
            Assert.DoesNotContain(serverWindow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.StartsWith('[') == true);
            Assert.DoesNotContain(sourcesWindow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.StartsWith('[') == true);
            await vm.SaveAsync();
            var saved = await store.LoadAsync();
            Assert.Equal(35, saved.Modes!.Single(item => item.Kind == ModeKind.WebSocketServer).Server!.ControlLimit);
            Assert.Equal(sources.Url, Assert.Single(saved.Settings.RemoteEndpoints!).Url);
            foreach (var path in Directory.EnumerateFiles(folder.Path, "*.json"))
            {
                var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                Assert.DoesNotContain("test-only", json);
                Assert.DoesNotContain(server.Token, json);
            }
            Assert.False(mode.CanStart);
            Assert.False(server.IsRunning);
            Assert.False(sources.Sources[0].IsConnected);
            Assert.DoesNotContain(vm.Devices, item => item.Device.Origin == DeviceOrigin.Remote);
        }
        finally
        {
            serverWindow.Close();
            sourcesWindow.Close();
            L10n.Current.SetLanguage(language);
        }
    }
}
