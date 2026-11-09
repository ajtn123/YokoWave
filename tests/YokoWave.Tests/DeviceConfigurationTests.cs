using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class DeviceConfigurationTests
{
    [Fact]
    public async Task DeviceHardwareSettingsApplyToEveryFrameAndDoNotAffectOtherDevices()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        var one = new DeviceDescriptor("one", "one", DeviceKind.DualChannel, 80, -40, ProtocolId: nameof(BluetoothProtocol.CoyoteV3));
        var two = one with { Id = "two", Name = "two" };
        var settings = new DeviceOutputSettings(one.Id, true, new(35, 75, RelativeStrength: true), new(200, 100, 90, 80));
        controller.LoadOutputSettings([settings]);
        await controller.ConnectAsync(one, TestContext.Current.CancellationToken);
        await controller.ConnectAsync(two, TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest[one.Id]);
        await controller.SendAsync(one.Id, new(90, 90, 20), TestContext.Current.CancellationToken);
        await controller.SendAsync(two.Id, new(90, 90, 20), TestContext.Current.CancellationToken);
        Assert.Equal(35, transport.Latest[one.Id].ChannelA);
        Assert.Equal(75, transport.Latest[one.Id].ChannelB);
        Assert.True(transport.Latest[one.Id].Gentle);
        Assert.Equal(settings.Balances, transport.Latest[one.Id].DgBalances);
        Assert.Equal(90, transport.Latest[two.Id].ChannelA);
        Assert.False(transport.Latest[two.Id].Gentle);
        var graph = new PipelineBuilder("limited");
        var level = graph.Add(BuiltInNodes.Constant, new ConstantNode(.9));
        var sink = graph.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(one.Id, Limit: 1));
        graph.Connect(level, "value", sink, "strength");
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(graph.Build(), [], new Dictionary<string, DeviceDescriptor> { [one.Id] = one });
        await using var session = new PipelineSession(controller);
        SessionRecord? record = null;
        session.Completed += (_, completed) => record = completed;
        await session.StartAsync(plan);
        await session.StopAsync();
        Assert.Equal(35, record!.PeakA);
    }

    [Fact]
    public async Task RememberedAuxiliaryMotorStaysOffDuringConnectionConfigurationAndStops()
    {
        var transport = new RecordingTransport();
        await using var controller = new DeviceController(transport);
        var device = new DeviceDescriptor("aux", "aux", DeviceKind.DualChannel, 80, -40, ProtocolId: nameof(BluetoothProtocol.YokonexV2));
        var auxiliary = Enum.GetValues<AuxiliaryMotorMode>().First(m => m != AuxiliaryMotorMode.Off);
        var settings = new DeviceOutputSettings(device.Id, Hardware: new(AuxiliaryMotor: auxiliary));
        controller.LoadOutputSettings([settings]);
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest[device.Id]);
        await controller.SetOutputSettingsAsync(device, settings, TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest[device.Id]);
        await controller.SendAsync(device.Id, new(0, 0, 20), TestContext.Current.CancellationToken);
        Assert.Equal(auxiliary, transport.Latest[device.Id].Hardware!.AuxiliaryMotor);
        await controller.StopAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutputFrame.Zero, transport.Latest[device.Id]);
    }

    [AvaloniaFact]
    public async Task ChannelsFollowTheSelectedModelAndHardwareControlsRemainInDeviceDetails()
    {
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
        await hardware.ApplyCommand.ExecuteAsync();
        var editor = vm.Pipelines.Selected!;
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
        Assert.False(hardware.ApplyCommand.CanExecute(null));
        hardware.LimitA = 80;
        Assert.Equal(35, hardware.LimitA);
        await controller.StopOwnedAsync(owner, TestContext.Current.CancellationToken);
        vm.Devices.Refresh();
        vm.Devices.SelectedDevice = dg;
        await vm.NavigateAsync("devices");
        var window = new Window { Width = 1500, Height = 950, Content = new MainView { DataContext = vm } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<DeviceHardwareOptionsView>().Single(v => v.IsEffectivelyVisible);
            Assert.Same(hardware, view.DataContext);
            Assert.Empty(view.GetVisualDescendants().OfType<Expander>());
            Assert.DoesNotContain(view.GetVisualAncestors(), c => c is DeviceCardView);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var screenshots = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
            Directory.CreateDirectory(screenshots);
            frame.Save(Path.Combine(screenshots, "device-hardware-options.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        var setting = Assert.Single(saved.Settings.DeviceOutputs!);
        Assert.Equal(dg.Id, setting.DeviceId);
        Assert.Equal(35, setting.Hardware!.ChannelALimit);
        Assert.Equal(200, setting.Balances!.FrequencyA);
    }

    [Fact]
    public void SequenceEditorsPreserveSignedSamplesAndNativeBufferOrderingWithoutJsonInput()
    {
        var changed = 0;
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

    [AvaloniaFact]
    public async Task ClearLocalDataStopsConnectionsRebuildsDefaultsAndCannotBeUndoneByTheOldViewModelSaving()
    {
        using var folder = new TestFolder();
        MainViewModel? replacement = null;
        var originalController = new DeviceController(new SimulationTransport());
        await using var original = new MainViewModel(originalController, new(folder.Path), replaceViewModel: vm => replacement = vm);
        await original.InitializeAsync();
        var device = original.Devices.Devices.First(d => d.Device.Kind == DeviceKind.DualChannel);
        await original.Devices.ToggleConnectionAsync(device);
        original.Devices.AutoConnect = true;
        var running = original.Pipelines.Selected!;
        running.RebindDevice("$device", device.Id);
        await running.StartAsync();
        await original.Waveforms.AddAsync(new(Guid.NewGuid(), "custom", WaveShape.Sine, 2, 20));
        await original.Pipelines.SaveAsync();
        var keep = Path.Combine(folder.Path, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep", TestContext.Current.CancellationToken);
        await original.Settings.ClearLocalDataCommand.ExecuteAsync();
        Assert.NotNull(replacement);
        await using var reset = replacement;
        Assert.Empty(originalController.Connections);
        Assert.False(running.Session.HoldsResources);
        Assert.False(reset.IsError);
        Assert.False(reset.Devices.AutoConnect);
        Assert.Empty(reset.Devices.AutoConnectDevices);
        Assert.All(reset.Devices.Devices, d => Assert.False(d.IsConnected));
        Assert.Equal(DefaultWaveformCatalog.Presets.Count, reset.Waveforms.Presets.Count);
        Assert.All(reset.Waveforms.Presets, p => Assert.True(p.IsBuiltIn));
        Assert.Empty(reset.History.Records);
        Assert.Single(reset.Pipelines.Pipelines);
        await original.SaveAsync();
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.False(saved.Settings.AutoConnect);
        Assert.Empty(saved.Presets);
        Assert.Equal("keep", await File.ReadAllTextAsync(keep, TestContext.Current.CancellationToken));
    }
}
