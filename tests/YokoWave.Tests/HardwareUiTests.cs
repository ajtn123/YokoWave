using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.Concurrent;
using YokoWave.App.ViewModels;
using YokoWave.App.Views;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;
using Xunit;

namespace YokoWave.Tests;

public sealed class HardwareUiTests
{
    [AvaloniaFact]
    public async Task RestoredRoutingRefreshesCompatibleChoicesWhenScanIdentifiesTheDevice()
    {
        using var folder = new TestFolder(); var store = new WorkspaceStore(folder.Path);
        await store.SaveAsync(new(WorkspaceData.CurrentSchema, [], [], new(UseBluetooth: true), Modes:
            [new(Guid.NewGuid(), "Saved", WaveformAId: DefaultWaveformCatalog.Presets[0].Id, TargetDeviceIds: ["CoyoteV3"])]));
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        var controller = new DeviceController(new SimulationTransport());
        await using var vm = new MainViewModel(controller, store, () => new BluetoothDeviceTransport(adapter));
        var view = new MainView { DataContext = vm }; var window = new Window { Content = view };
        try
        {
            window.Show();
            for (var i = 0; i < 200 && (vm.Devices.Count == 0 || vm.IsScanning); i++)
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, TestContext.Current.CancellationToken); }
            Assert.Equal(DefaultWaveformCatalog.Presets[0].Id, vm.SelectedPreset!.Preset.Id);
            var picker = view.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "PresetPicker");
            Assert.Equal(vm.Presets.Count, picker.ItemsSource!.Cast<PresetItem>().Count());
            Assert.Empty(controller.Connections); Assert.False(vm.IsRunning);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HardwarePanelsFilterRoutingLocalizeAndPersistOnlyModeSettings()
    {
        using var folder = new TestFolder(); var store = new WorkspaceStore(folder.Path);
        await store.SaveAsync(new(WorkspaceData.CurrentSchema, [], [], new(UseBluetooth: true)));
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3, BluetoothProtocol.YokonexMotor,
            BluetoothProtocol.YokonexPump, BluetoothProtocol.Paw, BluetoothProtocol.Civet);
        var previous = Logger.Sink; var errors = new BindingErrors(); Logger.Sink = errors;
        var language = L10n.Current.Language;
        Window? window = null;
        try
        {
            await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), store, () => new BluetoothDeviceTransport(adapter));
            await vm.InitializeAsync();
            var view = new MainView { DataContext = vm }; window = new Window { Content = view, Width = 1200, Height = 1000 };
            window.Show(); Dispatcher.UIThread.RunJobs();
            var coyote = vm.Devices.Single(device => device.Device.Profile?.Protocol == BluetoothProtocol.CoyoteV3);
            await vm.ToggleConnectionAsync(coyote); var waveMode = vm.SelectedMode;
            waveMode.Name = "V3 settings"; waveMode.AssignTarget(coyote.Id, true);
            Assert.Equal(1, waveMode.SelectedPreset!.Preset.FormatVersion);
            Assert.Equal(vm.Presets.Count, waveMode.PlaybackPresets.Count);
            waveMode.FrequencyBalanceA = 42; waveMode.WidthBalanceB = 201; waveMode.ChannelA = 12;
            Assert.True(waveMode.HasDgBalances); Assert.False(vm.HasElectrodeReadings);
            Assert.True(waveMode.CanStart);
            Dispatcher.UIThread.RunJobs(); view.GetVisualDescendants().OfType<DgBalanceView>().Single().BringIntoView(); Capture(window, "coyote-v3-settings.png");
            await vm.AddModeAsync(ModeKind.Waveform); var motorMode = vm.SelectedMode;
            var motor = vm.Devices.Single(device => device.Device.Profile?.Protocol == BluetoothProtocol.YokonexMotor);
            await vm.ToggleConnectionAsync(motor); Dispatcher.UIThread.RunJobs(); motorMode.AssignTarget(motor.Id, true);
            Assert.True(motorMode.HasChannelC); motorMode.ChannelC = 37;
            Assert.True(motorMode.CanStart);
            Assert.Equal(37, vm.ChannelC); Assert.True(view.GetVisualDescendants().OfType<Slider>().Single(slider => slider.Name == "ChannelCSlider").IsVisible);
            Assert.All(motorMode.PlaybackPresets.Where(item => item.Preset.Samples is not null), item =>
                Assert.All(item.Preset.Samples!, sample => Assert.True(sample.IsValid)));
            await vm.AddModeAsync(ModeKind.Pump); var pumpMode = vm.SelectedMode;
            var pump = vm.Devices.Single(device => device.Device.Profile?.Protocol == BluetoothProtocol.YokonexPump);
            Assert.True(pump.CanAssign); Assert.False(coyote.CanAssign);
            await vm.ToggleConnectionAsync(pump); pumpMode.AssignTarget(pump.Id, true);
            pumpMode.SelectedPumpDirection = pumpMode.PumpDirections.Single(option => option.Value == (int)PumpDirection.Reverse); pumpMode.WaterEnabled = true;
            Assert.False(vm.HasStrength); Assert.True(view.GetVisualDescendants().OfType<PumpModeView>().Single().IsVisible);
            Dispatcher.UIThread.RunJobs(); view.GetVisualDescendants().OfType<PumpModeView>().Single().BringIntoView(); Capture(window, "pump-control.png");
            var paw = vm.Devices.Single(device => device.Device.IsInput && device.Device.Profile?.Protocol == BluetoothProtocol.Paw);
            Assert.False(paw.IsOutput); Assert.False(paw.CanAssign);
            Assert.Throws<LocalizedOperationException>(() => pumpMode.AssignTarget(paw.Id, true));
            await vm.ToggleConnectionAsync(paw); Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasTelemetry); Assert.Contains(vm.TelemetryReadings, row => row.Label == L10n.Get("Telemetry.Acceleration"));
            vm.SelectedLanguage = vm.Languages.Single(option => option.Code == "en"); Dispatcher.UIThread.RunJobs();
            Assert.Equal("V3 settings", waveMode.Name); Assert.Equal(12, waveMode.ChannelA); Assert.Equal(42, waveMode.FrequencyBalanceA);
            Assert.Equal(37, motorMode.ChannelC); Assert.Equal(PumpDirection.Reverse, pumpMode.ToConfig().Pump!.Direction);
            Assert.Contains(vm.TelemetryReadings, row => row.Label == "Acceleration (raw)");
            await vm.SaveAsync(); var data = await store.LoadAsync();
            Assert.Equal(42, data.Modes!.Single(mode => mode.Id == waveMode.Id).DgBalances!.FrequencyA);
            Assert.Equal(201, data.Modes!.Single(mode => mode.Id == waveMode.Id).DgBalances!.WidthB);
            Assert.Equal(37, data.Modes!.Single(mode => mode.Id == motorMode.Id).ChannelC);
            Assert.True(data.Modes!.Single(mode => mode.Id == pumpMode.Id).Pump!.Water);
            Assert.DoesNotContain("FrequencyBalance", await File.ReadAllTextAsync(store.SettingsPath));
            Assert.DoesNotContain("Telemetry", await File.ReadAllTextAsync(store.FilePath));
            Assert.Empty(errors.Messages);
        }
        finally { window?.Close(); Logger.Sink = previous; L10n.Current.SetLanguage(language); }
    }

    [AvaloniaFact]
    public async Task MixedSourcePlaylistIsRetainedAndPlaysOnCoyote()
    {
        using var folder = new TestFolder(); var store = new WorkspaceStore(folder.Path);
        await store.SaveAsync(new(WorkspaceData.CurrentSchema, [], [], new(UseBluetooth: true)));
        var adapter = new HardwareGattAdapter(BluetoothProtocol.CoyoteV3);
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), store, () => new BluetoothDeviceTransport(adapter));
        await vm.InitializeAsync(); var device = Assert.Single(vm.Devices); await vm.ToggleConnectionAsync(device);
        var mode = vm.SelectedMode;
        mode.PlaylistCandidate = vm.Presets.First(item => item != mode.SelectedPreset && item.Preset.Samples!.Any(sample => sample.Channels is not null));
        mode.AddPlaylistWave(); var savedIds = mode.Playlist.Select(entry => entry.Id).ToArray();
        mode.SelectedPlaybackOrder = mode.PlaybackOrders.Single(option => option.Value == (int)PlaybackOrder.Sequential);
        mode.AssignTarget(device.Id, true);
        Assert.Equal(savedIds, mode.Playlist.Select(entry => entry.Id)); Assert.True(mode.CanStart);
        await mode.TogglePlaybackAsync(); Assert.True(mode.IsRunning); await mode.StopAsync();
        mode.Playlist[0].RemoveCommand.Execute(null); var remaining = Assert.Single(mode.Playlist).Id;
        mode.AssignTarget(device.Id, false); mode.AssignTarget(device.Id, true);
        Assert.Equal(remaining, Assert.Single(mode.Playlist).Id); Assert.True(mode.CanStart);
    }

    private static void Capture(Window window, string file)
    {
        Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
        Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, file), PngBitmapEncoderOptions.Default);
    }
    private sealed class BindingErrors : ILogSink
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) { if (IsEnabled(level, area)) Messages.Enqueue(messageTemplate); }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values) => Log(level, area, source, messageTemplate);
    }
}
