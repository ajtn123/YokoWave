using Avalonia.Headless.XUnit;
using YokoWave.App.ViewModels;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Tests.Support.Bluetooth;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class LocalDataResetTests
{
    [AvaloniaFact]
    public async Task ClearLocalDataStopsConnectionsRebuildsDefaultsAndCannotBeUndoneByTheOldViewModelSaving()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        MainViewModel? replacement = null;
        var originalController = new DeviceController(new SimulationTransport());
        await using var original = new MainViewModel(originalController, await folder.CreateSimulationStoreAsync(),
            () => new BluetoothDeviceTransport(new HardwareGattAdapter(BluetoothProtocol.CoyoteV3)), replaceViewModel: vm => replacement = vm);
        await original.InitializeAsync();
        var device = original.Devices.Devices.First(d => d.Device.Kind == DeviceKind.DualChannel);
        await original.Devices.ToggleConnectionAsync(device);
        original.Devices.AutoConnect = true;
        var running = original.Pipelines.Selected!;
        running.RebindDevice("$device", device.Id);
        await running.StartAsync();
        await original.Waveforms.AddAsync(new(Guid.NewGuid(), "custom", WaveShape.Sine, 2, 20));
        await original.Pipelines.SaveAsync();
        string keep = Path.Combine(folder.Path, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep", TestContext.Current.CancellationToken);
        await original.Settings.ClearLocalDataCommand.ExecuteAsync();
        Assert.NotNull(replacement);
        await using var reset = replacement;
        Assert.Empty(originalController.Connections);
        Assert.False(running.Session.HoldsResources);
        Assert.False(reset.IsError);
        Assert.True(reset.Devices.UsesLocalBluetooth);
        Assert.False(reset.Devices.AutoConnect);
        Assert.Empty(reset.Devices.AutoConnectDevices);
        Assert.All(reset.Devices.Devices, d => Assert.False(d.IsConnected));
        Assert.Equal(DefaultWaveformCatalog.Waveforms.Count, reset.Waveforms.Waveforms.Count);
        Assert.All(reset.Waveforms.Waveforms, p => Assert.True(p.IsBuiltIn));
        Assert.Empty(reset.History.Records);
        Assert.Single(reset.Pipelines.Pipelines);
        await original.SaveAsync();
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.False(saved.Settings.AutoConnect);
        Assert.True(saved.Settings.UseBluetooth);
        Assert.Empty(saved.Waveforms);
        Assert.Equal("keep", await File.ReadAllTextAsync(keep, TestContext.Current.CancellationToken));
    }
}
