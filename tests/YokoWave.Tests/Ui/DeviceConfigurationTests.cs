using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using YokoWave.Tests.Support.Bluetooth;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class DeviceConfigurationTests
{
    [AvaloniaFact]
    public async Task ChannelsFollowTheSelectedModelAndHardwareControlsRemainInDeviceDetails()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3, BluetoothProtocol.YokonexV2, BluetoothProtocol.YokonexMotor);
        var controller = new DeviceController(new SimulationTransport());
        await using var vm = new MainViewModel(controller, new(folder.Path), () => new BluetoothDeviceTransport(adapter));
        await vm.InitializeAsync();
        await vm.Devices.ChangeTransportAsync(true);
        await vm.Devices.ScanAsync();
        foreach (var device in vm.Devices.Devices)
            await vm.Devices.ToggleConnectionAsync(device);
        Dispatcher.UIThread.RunJobs();
        var dg = vm.Devices.Devices.Single(d => d.Device.Profile?.Protocol == BluetoothProtocol.CoyoteV3);
        var yoko = vm.Devices.Devices.Single(d => d.Device.Profile?.Protocol == BluetoothProtocol.YokonexV2);
        var motor = vm.Devices.Devices.Single(d => d.Device.Profile?.Protocol == BluetoothProtocol.YokonexMotor);
        var hardware = dg.HardwareOptions!;
        Assert.True(hardware.HasLimits && hardware.HasRelative && hardware.HasBalances);
        Assert.False(hardware.HasAuxiliary);
        Assert.True(yoko.HardwareOptions!.HasAuxiliary);
        Assert.False(yoko.HardwareOptions.HasBalances || yoko.HardwareOptions.HasRelative || yoko.HardwareOptions.HasLimits);
        Assert.False(motor.HardwareOptions!.HasAuxiliary || motor.HardwareOptions.HasLimits);
        hardware.LimitA = 35;
        hardware.FrequencyA = 200;
        var editor = vm.Pipelines.Selected!;
        editor.RebindDevice("$device", dg.Id);
        await editor.StartAsync();
        Assert.Equal(35, controller.GetOutputSettings(dg.Id).Hardware!.ChannelALimit);
        Assert.Equal(200, controller.GetOutputSettings(dg.Id).Balances!.FrequencyA);
        await editor.StopAsync();
        Dispatcher.UIThread.RunJobs();
        var output = editor.Nodes.OfType<ChannelOutputEditorViewModel>().First();
        output.SelectedDevice = output.Devices.Single(d => d.Id == dg.Id);
        Assert.Equal(new[] { 0, 1 }, output.Channels.Select(c => c.Value));
        output.SelectedDevice = output.Devices.Single(d => d.Id == motor.Id);
        Assert.Equal(new[] { 0, 1, 2 }, output.Channels.Select(c => c.Value));
        output.SelectedChannel = output.Channels[2];
        Assert.Equal(2, ((ChannelOutputNode)editor.Registry.Get(output.Type).Read(output.ToDefinition().Configuration)).Channel);
        Assert.DoesNotContain("Hardware", JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options));
        Assert.DoesNotContain("Gentle", JsonSerializer.Serialize(editor.ToDocument(), PipelineJson.Options));
        var owner = Guid.NewGuid();
        await controller.AcquireTargetsAsync(owner, [dg.Id], TestContext.Current.CancellationToken);
        vm.Devices.Refresh();
        Assert.False(hardware.CanEdit);
        hardware.LimitA = 80;
        Assert.Equal(35, hardware.LimitA);
        await controller.StopOwnedAsync(owner, TestContext.Current.CancellationToken);
        vm.Devices.Refresh();
        vm.Devices.SelectedDevice = dg;
        await vm.NavigateAsync("devices");
        var window = new Window
        {
            Width = 1500,
            Height = 950,
            Content = new MainView
            {
                DataContext = vm
            }
        };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<DeviceHardwareOptionsView>().Single(v => v.IsEffectivelyVisible);
            Assert.Same(hardware, view.DataContext);
            Assert.Empty(view.GetVisualDescendants().OfType<Expander>());
            Assert.DoesNotContain(view.GetVisualDescendants(), c => c.GetType() == typeof(Button));
            Assert.DoesNotContain(view.GetVisualAncestors(), c => c is DeviceCardView);
            UiArtifacts.Capture(window, "device-hardware-options.png");
        }
        finally
        {
            window.Close();
        }

        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        var setting = Assert.Single(saved.Settings.DeviceOutputs!);
        Assert.Equal(dg.Id, setting.DeviceId);
        Assert.Equal(35, setting.Hardware!.ChannelALimit);
        Assert.Equal(200, setting.Balances!.FrequencyA);
    }

    [Fact]
    public void SequenceEditorsPreserveSignedSamplesAndNativeBufferOrderingWithoutJsonInput()
    {
        int changed = 0;
        var sequence = new WaveformSamplesEditorViewModel([new(.4, 30, .5, [-.8, .3]), new(.6, 50, .7, [.2])], () => changed++);
        var first = sequence.Rows[0];
        first.ChannelA = -.5;
        sequence.Selected = first;
        sequence.MoveDownCommand.Execute(null);
        var samples = sequence.Snapshot();
        Assert.Equal(new[] { .2 }, samples[0].Channels);
        Assert.Equal(new[] { -.5, .3 }, samples[1].Channels);
        Assert.True(changed > 0);
        var native = new NativeProgramEditorViewModel(new(NativeProgramKind.ElectricalBuffered, BufferA: [new(20, 40), new(50, 60)], BufferB: [new()]), () => true);
        native.BufferA.Selected = native.BufferA.Rows[1];
        native.BufferA.MoveUpCommand.Execute(null);
        native.BufferA.Rows[0].PulseWidth = 80;
        Assert.Equal(new FrequencyPulse(50, 80), native.Settings.BufferA![0]);
        Assert.Equal(new FrequencyPulse(20, 40), native.Settings.BufferA[1]);
    }
}
