using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Logging;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using YokoWave.App.Views.Pages;
using YokoWave.App.Views.Components;
using YokoWave.App.ViewModels;
using YokoWave.App.Views;
using YokoWave.App.Controls;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Core.Bluetooth;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(YokoWave.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace YokoWave.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseHarfBuzz()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task PlaybackButtonRequiresEveryAssignedDeviceAndUpdatesWhenTheModeChanges()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm }; var window = new Window { Content = view, Width = 1200, Height = 1000 };
        try
        {
            window.Show(); await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
            var playback = Find<Button>(view, "PlaybackButton"); var first = vm.SelectedMode;
            Assert.False(playback.IsEffectivelyEnabled);
            await vm.ToggleConnectionAsync(vm.Devices[0]);
            Assert.False(playback.IsEffectivelyEnabled); // Connection alone does not assign the device.
            first.AssignTarget(vm.Devices[0].Id, true); Dispatcher.UIThread.RunJobs();
            Assert.True(playback.IsEffectivelyEnabled);
            first.AssignTarget(vm.Devices[1].Id, true); Dispatcher.UIThread.RunJobs();
            Assert.False(playback.IsEffectivelyEnabled); // A partially connected target set cannot start.
            await vm.TogglePlaybackAsync(); Assert.False(first.IsRunning); Assert.True(vm.IsError);
            await vm.ToggleConnectionAsync(vm.Devices[1]); Dispatcher.UIThread.RunJobs();
            Assert.True(playback.IsEffectivelyEnabled);
            await vm.AddModeAsync(ModeKind.Touch); Dispatcher.UIThread.RunJobs();
            Assert.False(playback.IsEffectivelyEnabled);
            vm.SelectedMode = first; Dispatcher.UIThread.RunJobs(); Assert.True(playback.IsEffectivelyEnabled);
            Assert.Equal(0, first.ChannelA); Assert.Equal(0, first.ChannelB);
            await vm.TogglePlaybackAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(first.IsRunning); Assert.True(playback.IsEffectivelyEnabled); // Stop remains available at zero strength.
            await vm.TogglePlaybackAsync(); Assert.False(first.IsRunning);
            await vm.ToggleConnectionAsync(vm.Devices[0]); Dispatcher.UIThread.RunJobs();
            Assert.False(playback.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ExplicitDisconnectClosesAFailedLinkAndReconnectStartsWithANewConnection()
    {
        using var folder = new TestFolder(); var adapter = new FakeBluetoothAdapter();
        await new WorkspaceStore(folder.Path).SaveAsync(new(5, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter));
        await vm.InitializeAsync(); var device = Assert.Single(vm.Devices); await vm.ToggleConnectionAsync(device);
        vm.SelectedMode.AssignTarget(device.Id, true); await vm.TogglePlaybackAsync();
        var oldLink = adapter.Links[device.Id]; oldLink.FailWrites = true;
        await Assert.ThrowsAsync<LocalizedAggregateException>(() => vm.ToggleConnectionAsync(device));
        Assert.True(oldLink.Closed); Assert.False(device.IsConnected); Assert.False(vm.IsRunning);
        Assert.True(vm.SelectedMode.CanEditRouting); Assert.False(vm.PlaybackCommand.CanExecute(null));
        await vm.ToggleConnectionAsync(device);
        Assert.NotSame(oldLink, adapter.Links[device.Id]); Assert.True(vm.PlaybackCommand.CanExecute(null));
        await vm.TogglePlaybackAsync(); Assert.True(vm.IsRunning);
        Assert.Equal(0, device.OutputA); Assert.Equal(0, device.OutputB);
        await vm.StopAsync();
    }

    [AvaloniaFact]
    public async Task GroupedDevicesHaveOneModeEvenWhileIdleAndUseTypeSpecificControls()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 1000 };
        window.Show(); await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        var wave = vm.SelectedMode; wave.Name = "Wave instance";
        wave.SelectedPlaybackOrder = wave.PlaybackOrders.First(option => option.Value == (int)PlaybackOrder.Random);
        wave.AssignTarget(vm.Devices[0].Id, true);
        await vm.AddModeAsync(true); var touch = vm.SelectedMode; touch.Name = "Touch instance";
        Assert.ThrowsAny<InvalidOperationException>(() => touch.AssignTarget(vm.Devices[0].Id, true));
        Assert.Single(wave.TargetIds); Assert.Empty(touch.TargetIds); Assert.Equal(4, vm.UnassignedDevices.Count);
        Assert.False(Find<WaveformModeView>(view, "WaveformModePanel").IsVisible);
        Assert.True(Find<Border>(view, "TouchModePanel").IsVisible);
        var detach = view.GetVisualDescendants().OfType<Button>().Single(button => button.Command == wave.Targets[0].DetachCommand);
        Click(window, detach); Assert.Equal(5, vm.UnassignedDevices.Count);
        var assign = view.GetVisualDescendants().OfType<Button>().Single(button => button.Command == vm.Devices[0].AssignCommand);
        Click(window, assign); Assert.Contains(vm.Devices[0].Id, touch.TargetIds); Assert.Empty(wave.TargetIds);
        Assert.Equal(4, vm.UnassignedDevices.Count);
        Click(window, view.GetVisualDescendants().OfType<Button>().Single(button => button.Command == wave.SelectCommand));
        Assert.Same(wave, vm.SelectedMode);
        Assert.True(Find<WaveformModeView>(view, "WaveformModePanel").IsVisible);
        Assert.False(Find<Border>(view, "TouchModePanel").IsVisible);
        Assert.Same(wave.SelectedPlaybackOrder, Find<ComboBox>(view, "PlaybackOrderPicker").SelectedItem);
        vm.SelectedMode = touch; vm.SelectedMode = wave;
        Assert.Equal((int)PlaybackOrder.Random, wave.SelectedPlaybackOrder.Value);
        Assert.Same(wave.SelectedPlaybackOrder, Find<ComboBox>(view, "PlaybackOrderPicker").SelectedItem);
        Assert.Equal("Touch instance", touch.Name);
        await vm.SaveAsync();
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Single(saved.Modes!.SelectMany(mode => mode.TargetDeviceIds!));
        Assert.Equal(vm.Devices[0].Id, Assert.Single(saved.Modes!.Single(mode => mode.Id == touch.Id).TargetDeviceIds!));
        Capture(window, "grouped-modes.png");
        window.Width = 600; Dispatcher.UIThread.RunJobs();
        Find<TextBox>(view, "ModeNameInput").BringIntoView(); Capture(window, "grouped-modes-compact.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task CustomPlaylistSupportsUiOrderingPersistenceAndStartsAtItsFirstWave()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 1000 };
        window.Show(); await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        var mode = vm.SelectedMode;
        var waveformView = Find<WaveformModeView>(view, "WaveformModePanel");
        var candidate = Find<ComboBox>(waveformView, "PlaylistWavePicker");
        var add = Find<Button>(waveformView, "AddPlaylistWaveButton");
        candidate.SelectedItem = vm.Presets[2]; Click(window, add);
        candidate.SelectedItem = vm.Presets[1]; Click(window, add);
        Assert.Equal(new[] { vm.Presets[0].Preset.Id, vm.Presets[2].Preset.Id, vm.Presets[1].Preset.Id }, mode.Playlist.Select(entry => entry.Id));
        var last = mode.Playlist[2];
        Click(window, view.GetVisualDescendants().OfType<Button>().Single(button => button.Command == last.MoveUpCommand));
        var first = mode.Playlist[0];
        Click(window, view.GetVisualDescendants().OfType<Button>().Single(button => button.Command == first.RemoveCommand));
        Assert.Equal(new[] { vm.Presets[1].Preset.Id, vm.Presets[2].Preset.Id }, mode.Playlist.Select(entry => entry.Id));
        candidate.SelectedItem = vm.Presets[1]; Assert.False(add.IsEffectivelyEnabled);
        vm.SelectedPreset = vm.Presets[3];
        vm.SelectedPlaybackOrder = vm.PlaybackOrders.First(option => option.Value == (int)PlaybackOrder.Sequential);
        await vm.ToggleConnectionAsync(vm.Devices[0]); mode.AssignTarget(vm.Devices[0].Id, true);
        await vm.TogglePlaybackAsync();
        Assert.Equal(vm.Presets[1].Preset.Id, vm.SelectedPreset!.Preset.Id);
        Assert.False(last.MoveUpCommand.CanExecute(null));
        Assert.False(last.RemoveCommand.CanExecute(null));
        Assert.False(add.IsEffectivelyEnabled);
        await vm.NavigateAsync("waves");
        Find<ListBox>(view, "WaveList").SelectedItem = vm.Presets[3];
        Assert.Same(vm.Presets[3], vm.LibrarySelectedPreset);
        Assert.Equal(vm.Presets[1].Preset.Id, vm.SelectedPreset.Preset.Id);
        Assert.False(vm.UseWaveCommand.CanExecute(null));
        vm.EditWave(); await vm.SaveWaveAsync();
        Assert.False(vm.LibrarySelectedPreset!.IsBuiltIn);
        Assert.True(mode.IsRunning); Assert.Equal(vm.Presets[1].Preset.Id, vm.SelectedPreset.Preset.Id);
        await vm.DeleteWaveAsync(); await vm.NavigateAsync("workspace");
        Assert.Equal(2, mode.Playlist.Count);
        await vm.StopAsync(); await vm.SaveAsync();
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(mode.Playlist.Select(entry => entry.Id), Assert.Single(saved.Modes!).PlaylistWaveformIds);
        window.RequestedThemeVariant = ThemeVariant.Dark;
        waveformView.GetVisualDescendants().OfType<ModePlaylistView>().Single().BringIntoView(); Capture(window, "custom-playlist-dark.png");
        window.Close();
        await using var restored = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        await restored.InitializeAsync();
        Assert.Equal(mode.Playlist.Select(entry => entry.Id), restored.SelectedMode.Playlist.Select(entry => entry.Id));
        Assert.False(restored.IsRunning);
        while (restored.SelectedMode.Playlist.Count > 0) restored.SelectedMode.Playlist[0].RemoveCommand.Execute(null);
        Assert.Null(restored.SelectedPreset);
        await restored.ToggleConnectionAsync(restored.Devices.Single(device => restored.SelectedMode.TargetIds.Contains(device.Id)));
        Assert.False(restored.PlaybackCommand.CanExecute(null));
        await restored.TogglePlaybackAsync(); Assert.False(restored.IsRunning);
        Assert.True(restored.IsError); Assert.Equal(L10n.Get("Error.EmptyPlaylist"), restored.Status);
    }

    [AvaloniaFact]
    public async Task ModesOwnConfigurationAndTargetsAndRunIndependentlyAcrossNavigationAndRestart()
    {
        using var folder = new TestFolder(); var transport = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 1100 };
        window.Show(); await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        foreach (var device in vm.Devices.Take(4)) await vm.ToggleConnectionAsync(device);
        var first = vm.SelectedMode; first.Name = "Mode one";
        var assign = view.GetVisualDescendants().OfType<Button>().Single(button => button.Command == vm.Devices[0].AssignCommand);
        Click(window, assign);
        Assert.Contains(vm.Devices[0].Id, first.TargetIds);
        first.ChannelA = 20; first.SafetyLimit = 25; await first.TogglePlaybackAsync();
        await vm.AddModeAsync(false); var second = vm.SelectedMode; second.Name = "Mode two";
        second.AssignTarget(vm.Devices[1].Id, true); second.ChannelA = 40; second.SafetyLimit = 50;
        await second.TogglePlaybackAsync();
        await vm.AddModeAsync(false); var third = vm.SelectedMode; third.Name = "Mode three";
        third.AssignTarget(vm.Devices[2].Id, true); third.AssignTarget(vm.Devices[3].Id, true);
        third.ChannelA = 15; third.SafetyLimit = 18; await third.TogglePlaybackAsync(); await vm.TickAsync();
        Assert.Equal(20, transport.Outputs[vm.Devices[0].Id].ChannelA);
        Assert.Equal(40, transport.Outputs[vm.Devices[1].Id].ChannelA);
        Assert.Equal(15, transport.Outputs[vm.Devices[2].Id].ChannelA);
        Assert.Equal(15, transport.Outputs[vm.Devices[3].Id].ChannelA);
        vm.SelectedMode = first; await vm.SelectDeviceAsync(vm.Devices[3]);
        Assert.Equal(20, vm.ChannelA); Assert.Equal(25, vm.SafetyLimit);
        vm.SelectedMode = second; Assert.Equal(40, vm.ChannelA); Assert.Equal(50, vm.SafetyLimit);
        await vm.NavigateAsync("help"); await vm.TickAsync();
        Assert.All(vm.Modes, mode => Assert.True(mode.IsRunning));
        await first.StopAsync(); await vm.TickAsync();
        Assert.Equal(0, transport.Outputs[vm.Devices[0].Id].ChannelA);
        Assert.Equal(40, transport.Outputs[vm.Devices[1].Id].ChannelA);
        Assert.Equal(15, transport.Outputs[vm.Devices[3].Id].ChannelA);
        await vm.AddModeAsync(true); var fourth = vm.SelectedMode;
        Assert.ThrowsAny<InvalidOperationException>(() => fourth.AssignTarget(vm.Devices[1].Id, true));
        await fourth.TogglePlaybackAsync(); Assert.False(fourth.IsRunning);
        Assert.True(vm.IsError); Assert.Equal(L10n.Get("Error.SelectConnected"), vm.Status);
        Assert.True(second.IsRunning); Assert.Equal(40, transport.Outputs[vm.Devices[1].Id].ChannelA);
        await vm.NavigateAsync("workspace");
        vm.SelectedMode = third;
        Find<TextBox>(view, "ModeNameInput").BringIntoView(); Capture(window, "independent-modes.png");
        await vm.StopAllModesAsync();
        Assert.All(transport.Outputs.Values, output => Assert.Equal(0, output.ChannelA));
        await vm.SaveAsync();
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(4, saved.Modes!.Count); Assert.Equal(20, saved.Modes.Single(mode => mode.Id == first.Id).ChannelA);
        Assert.Equal(2, saved.Modes.Single(mode => mode.Id == third.Id).TargetDeviceIds!.Count);
        Assert.All(saved.Sessions, record => Assert.NotNull(record.ModeId));
        window.Close();
        await using var restored = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        await restored.InitializeAsync(); Assert.Equal(4, restored.Modes.Count);
        Assert.All(restored.Modes, mode => Assert.False(mode.IsRunning));
        Assert.Equal(40, restored.Modes.Single(mode => mode.Id == second.Id).ChannelA);
    }

    [AvaloniaFact]
    public async Task DisconnectionStopsOnlyTheOwningModeAndItsOtherTargets()
    {
        using var folder = new TestFolder(); var adapter = new FakeBluetoothAdapter();
        adapter.Devices.Add(new("ble:000000000002", "YCY-DJ", -55));
        adapter.Devices.Add(new("ble:000000000003", "YCY-DJ", -60));
        await new WorkspaceStore(folder.Path).SaveAsync(new(3, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter));
        await vm.InitializeAsync();
        foreach (var device in vm.Devices) await vm.ToggleConnectionAsync(device);
        var first = vm.SelectedMode;
        first.AssignTarget(vm.Devices[0].Id, true); first.AssignTarget(vm.Devices[1].Id, true); first.ChannelA = 20;
        await first.TogglePlaybackAsync();
        await vm.AddModeAsync(false); var second = vm.SelectedMode;
        second.AssignTarget(vm.Devices[2].Id, true); second.ChannelA = 35;
        await second.TogglePlaybackAsync(); await vm.TickAsync();
        await Task.Run(adapter.Links[vm.Devices[0].Id].LoseConnection, TestContext.Current.CancellationToken);
        await WaitForAsync(() => !first.IsRunning && !vm.Devices[0].IsConnected && vm.IsError);
        Assert.True(second.IsRunning); await vm.TickAsync();
        Assert.Equal(V2Protocol.Stop(), adapter.Links[vm.Devices[1].Id].Writes[^1]);
        Assert.Equal(35, vm.Devices[2].OutputA);
        Assert.Equal(first.Id, Assert.Single(vm.Sessions).Record.ModeId);
    }

    [AvaloniaFact]
    public async Task WaveUsedByAnotherModeCannotBeEditedOrDeleted()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        await vm.InitializeAsync(); await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.EditorName = "Shared custom"; await vm.SaveWaveAsync(); var custom = vm.LibrarySelectedPreset!;
        var first = vm.SelectedMode; first.SelectedPreset = custom; first.AssignTarget(vm.Devices[0].Id, true);
        await first.TogglePlaybackAsync();
        await vm.AddModeAsync(false); var second = vm.SelectedMode; second.SelectedPreset = custom;
        Assert.False(vm.DeleteWaveCommand.CanExecute(null));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(vm.DeleteWaveAsync);
        vm.EditWave(); vm.EditorName = "Changed custom";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(vm.SaveWaveAsync);
        Assert.True(first.IsRunning); Assert.Same(custom, first.SelectedPreset);
        await first.StopAsync();
        Assert.True(vm.DeleteWaveCommand.CanExecute(null));
        await vm.SaveWaveAsync();
        Assert.Equal("Changed custom", first.SelectedPreset!.Name);
        Assert.Same(first.SelectedPreset, second.SelectedPreset);
    }

    [AvaloniaFact]
    public async Task EstimControlsSupportIndependentWavesPauseAndPointerReleasedBoost()
    {
        using var folder = new TestFolder();
        var adapter = new FakeBluetoothAdapter();
        await new WorkspaceStore(folder.Path).SaveAsync(new(2, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 1000 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 1 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        vm.SeparateWaveforms = true;
        Find<ComboBox>(view, "ChannelBWavePicker").SelectedItem = vm.Presets[1];
        Assert.Equal(vm.Presets[1].Preset, Find<WaveformPlot>(view, "MainPlot").PresetB);
        vm.ChannelA = 20; vm.ChannelB = 10;
        await vm.TogglePlaybackAsync(); await vm.TickAsync();
        Assert.NotEqual(adapter.Links[vm.SelectedDevice!.Id].Writes[^1][6], adapter.Links[vm.SelectedDevice.Id].Writes[^1][10]);
        var boost = Find<HoldButton>(view, "BoostButton");
        Find<ToggleButton>(view, "StrengthOptionsToggle").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        boost.BringIntoView(); Dispatcher.UIThread.RunJobs();
        var position = boost.TranslatePoint(new Point(boost.Bounds.Width / 2, boost.Bounds.Height / 2), window)!.Value;
        window.MouseDown(position, MouseButton.Left);
        Assert.True(boost.IsHeld, $"Boost was not held at {position}; bounds {boost.Bounds}, enabled {boost.IsEffectivelyEnabled}.");
        Assert.True(vm.IsBoostHeld);
        await WaitForAsync(() => vm.SelectedDevice.OutputA == 30);
        Assert.Equal(20, vm.SelectedDevice.OutputB);
        window.MouseUp(new Point(0, 0), MouseButton.Left);
        await WaitForAsync(() => vm.SelectedDevice.OutputA == 20);
        Assert.Equal(10, vm.SelectedDevice.OutputB);
        Click(window, Find<Button>(view, "PauseButton")); await WaitForAsync(() => vm.IsPaused);
        var clock = vm.Remaining;
        await Task.Delay(200, TestContext.Current.CancellationToken); await vm.TickAsync();
        Assert.Equal(clock, vm.Remaining); Assert.Equal(0, vm.SelectedDevice.OutputA);
        Assert.False(boost.IsEffectivelyEnabled);
        Click(window, Find<Button>(view, "PauseButton")); await WaitForAsync(() => !vm.IsPaused);
        await vm.TickAsync(); Assert.True(vm.IsRunning); Assert.Equal(20, vm.SelectedDevice.OutputA);
        Capture(window, "estim-automation.png");
        var pane = Find<StackPanel>(view, "ControlPane");
        var right = pane.TranslatePoint(new Point(pane.Bounds.Width, 0), window)!.Value.X;
        Assert.True(right <= window.Width - ((Thickness)view.FindResource("Margin.Content")!).Right,
            $"Control pane extends beyond the content area: {right}.");
        await vm.StopAsync();
        Assert.NotNull(Assert.Single(vm.Sessions).Record.ChannelBWaveformId);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TouchControlRecordsReplaysAndSavesACustomElectricalWaveform()
    {
        using var folder = new TestFolder();
        var transport = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 1000 };
        window.Show(); await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, false);
        await vm.AddModeAsync(true); vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true); vm.ChannelA = 20;
        vm.SelectedTouchPlayback = vm.TouchPlaybackModes.First(option => option.Value == (int)TouchPlayback.RecordedLoop);
        await vm.TogglePlaybackAsync(); await vm.TickAsync();
        Assert.Equal(0, transport.Outputs[vm.SelectedDevice!.Id].ChannelA);
        var pad = Find<ElectricalPad>(view, "FrequencyPad"); pad.BringIntoView(); Dispatcher.UIThread.RunJobs();
        var position = pad.TranslatePoint(new Point(pad.Bounds.Width * 0.5, pad.Bounds.Height * 0.25), window)!.Value;
        window.MouseDown(position, MouseButton.Left);
        await WaitForAsync(() => vm.HasRecording);
        Assert.Equal(75, vm.TouchPulseWidth);
        Assert.InRange(vm.TouchFrequency, 50, 51);
        window.MouseUp(position, MouseButton.Left);
        await vm.TickAsync(); Assert.Equal(20, transport.Outputs[vm.SelectedDevice.Id].ChannelA);
        Capture(window, "estim-touch.png");
        await vm.StopAsync(); vm.TouchRecordingName = "Local touch recording";
        Assert.True(vm.CanSaveRecording);
        await vm.SaveRecordingAsync();
        Assert.Equal(WaveformFamily.Electrical, vm.SelectedPreset!.Preset.Family);
        Assert.InRange(vm.SelectedPreset.Preset.Samples!.Length, 2, SessionEngine.MaxRecordingFrames);
        Assert.Equal("Local touch recording", Assert.Single((await new WorkspaceStore(folder.Path).LoadAsync()).Presets).Name);
        Assert.True(Assert.Single(vm.Sessions).Record.TouchControl);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FirmwareReadEnablesConfirmedLightControlAndSettingsKeepEstimOptions()
    {
        using var folder = new TestFolder();
        var adapter = new FakeBluetoothAdapter();
        await new WorkspaceStore(folder.Path).SaveAsync(new(2, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 900 };
        window.Show(); await WaitForAsync(() => vm.Devices.Count == 1 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        var read = Find<Button>(view, "ReadDeviceFeaturesButton");
        Assert.True(read.IsEffectivelyEnabled); Assert.False(vm.CanControlLight);
        Click(window, read); await WaitForAsync(() => vm.CanControlLight);
        Click(window, Find<Button>(view, "ToggleLightButton"));
        await WaitForAsync(() => vm.LightLabel == L10n.Get("Device.LightOff"));
        Capture(window, "estim-device-features.png");
        await vm.NavigateAsync("settings");
        vm.GentleMode = true; vm.ShowContacts = false;
        vm.SelectedPlaybackOrder = vm.PlaybackOrders.First(option => option.Value == (int)PlaybackOrder.Sequential);
        vm.SelectedStrengthMode = vm.StrengthModes.First(option => option.Value == (int)StrengthMode.StepIncrease);
        vm.StrengthInterval = 4; vm.StrengthStep = 2; vm.StrengthCeiling = 45;
        await vm.SaveSettingsAsync();
        var settings = Assert.Single((await new WorkspaceStore(folder.Path).LoadAsync()).Modes!).Estim!;
        Assert.True(settings.Gentle); Assert.False(settings.ShowContacts);
        Assert.Equal(PlaybackOrder.Sequential, settings.Order);
        Assert.Equal(4, settings.Strength!.IntervalSeconds); Assert.Equal(2, settings.Strength.Step);
        Assert.Equal(45, settings.Strength.Ceiling);
        window.Close();
    }

    [AvaloniaFact]
    public async Task HelpNavigationKeepsModesRunningRetainsSelectionsAndLocalizesInBothLayouts()
    {
        using var folder = new TestFolder();
        var originalLanguage = L10n.Current.Language;
        Window? window = null;
        try
        {
            var transport = new SimulationTransport();
            await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
            var view = new MainView { DataContext = vm };
            window = new Window { Content = view, Width = 1000, Height = 900 };
            window.Show();
            await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
            vm.SelectedLanguage = vm.Languages.First(language => language.Code == "zh-CN");
            await vm.ToggleConnectionAsync(vm.Devices[0]);
            vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
            var selectedDevice = vm.SelectedDevice;
            var selectedPreset = vm.SelectedPreset;
            vm.ChannelA = 20;
            vm.EditorName = "Help navigation";
            await vm.TogglePlaybackAsync();
            await vm.TickAsync();
            Click(window, Find<Button>(view, "HelpNavigationButton"));
            await WaitForAsync(() => vm.IsHelp);
            Assert.True(vm.IsRunning);
            Assert.Empty(vm.Sessions);
            Assert.True(Find<HelpView>(view, "HelpPage").IsVisible);
            Assert.False(Find<SettingsView>(view, "SettingsPage").IsVisible);
            Assert.Equal("帮助", Find<TextBlock>(view, "PageTitleText").Text);
            var stopInfo = Find<TextBlock>(view, "HelpStopInfoText");
            Assert.Equal(L10n.Get("Help.StopBehavior"), stopInfo.Text);
            Assert.DoesNotContain(Find<SettingsView>(view, "SettingsPage").GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == stopInfo.Text);
            stopInfo.BringIntoView();
            Capture(window, "help-zh-CN.png");

            vm.SelectedLanguage = vm.Languages.First(language => language.Code == "en");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Help", Find<TextBlock>(view, "PageTitleText").Text);
            Assert.Equal(L10n.Current.English["Help.StopBehavior"], stopInfo.Text);
            Assert.Same(selectedDevice, vm.SelectedDevice);
            Assert.Same(selectedPreset, vm.SelectedPreset);
            Assert.Equal(20, vm.ChannelA);
            Assert.Equal("Help navigation", vm.EditorName);

            window.Width = 600;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<StackPanel>(view, "CompactNav").IsVisible);
            await vm.NavigateAsync("settings");
            Click(window, Find<Button>(view, "CompactHelpButton"));
            await WaitForAsync(() => vm.IsHelp);
            stopInfo.BringIntoView();
            Capture(window, "help-en-compact.png");
        }
        finally
        {
            window?.Close();
            L10n.Current.SetLanguage(originalLanguage);
        }
    }

    [AvaloniaFact]
    public async Task ZeroStrengthSessionStartsFromButtonAndAcceptsLiveStrengthChanges()
    {
        using var folder = new TestFolder();
        var adapter = new FakeBluetoothAdapter();
        await new WorkspaceStore(folder.Path).SaveAsync(new(2, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 900 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 1 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        Assert.Equal(0, vm.ChannelA);
        Assert.Equal(0, vm.ChannelB);
        var link = adapter.Links[vm.SelectedDevice!.Id];
        var button = Find<Button>(view, "PlaybackButton");
        Click(window, button);
        await WaitForAsync(() => vm.IsRunning);
        await vm.TickAsync();
        Assert.False(vm.IsError);
        Assert.True(Find<ProgressBar>(view, "TimerProgressBar").IsVisible);
        Assert.Equal(0, vm.SelectedDevice.OutputA);
        Assert.Equal(0, vm.SelectedDevice.OutputB);
        Assert.Equal(V2Protocol.Stop(), link.Writes[^1]);

        var channelA = Find<Slider>(view, "ChannelASlider");
        var channelB = Find<Slider>(view, "ChannelBSlider");
        channelA.Value = 25;
        channelB.Value = 15;
        await vm.TickAsync();
        Assert.Equal(25, vm.SelectedDevice.OutputA);
        Assert.Equal(15, vm.SelectedDevice.OutputB);
        channelA.Value = channelB.Value = 0;
        await vm.TickAsync();
        Assert.Equal(V2Protocol.Stop(), link.Writes[^1]);
        Assert.True(vm.IsRunning);
        Assert.Empty(vm.Sessions);
        Click(window, button);
        await WaitForAsync(() => !vm.IsRunning);
        var record = Assert.Single(vm.Sessions).Record;
        Assert.Equal(25, record.PeakA);
        Assert.Equal(15, record.PeakB);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TimerCountsSecondsAndLiveWaveChangeDoesNotRestartTheSession()
    {
        using var folder = new TestFolder();
        var transport = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 900 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        var slider = Find<Slider>(view, "DurationSlider");
        var progress = Find<ProgressBar>(view, "TimerProgressBar");
        slider.Value = 3;
        Assert.Equal("00:03", Find<TextBlock>(view, "SessionClockText").Text);
        Assert.Equal("00:03", Find<TextBlock>(view, "TimerReadoutText").Text);
        Assert.True(slider.IsVisible);
        Assert.False(progress.IsVisible);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        vm.ChannelA = 20;
        await vm.TogglePlaybackAsync();
        Assert.False(Find<CheckBox>(view, "TimerEnabledToggle").IsEffectivelyEnabled);
        Assert.False(slider.IsVisible);
        Assert.True(progress.IsVisible);
        Assert.InRange(progress.Value, 0.9, 1);
        await WaitForAsync(() => vm.Remaining == "00:02");
        var remainingProgress = progress.Value;
        Assert.InRange(remainingProgress, 0.3, 0.7);
        var picker = Find<ComboBox>(view, "PresetPicker");
        Assert.True(picker.IsEffectivelyEnabled);
        picker.SelectedItem = vm.Presets[1];
        await vm.TickAsync();
        Assert.True(vm.IsRunning);
        Assert.Equal("00:02", vm.Remaining);
        Assert.InRange(progress.Value, 0.3, remainingProgress);
        Assert.Equal("00:02", Find<TextBlock>(view, "TimerReadoutText").Text);
        Assert.Empty(vm.Sessions);
        Assert.Equal(vm.Presets[1].Preset.Samples![0], transport.Outputs["sim-dual-01"].ReferenceSample);
        Find<Button>(view, "PlaybackButton").BringIntoView();
        Capture(window, "timer-live-wave.png");
        await WaitForAsync(() => !vm.IsRunning);
        Assert.Equal("00:00", Find<TextBlock>(view, "SessionClockText").Text);
        Assert.Equal("Reason.Timer", Assert.Single(vm.Sessions).Record.Reason);
        Assert.Equal(0, transport.Outputs["sim-dual-01"].ChannelA);
        Assert.True(slider.IsVisible);
        Assert.False(progress.IsVisible);
        Assert.Equal(3, slider.Value);
        slider.Value = 7200;
        Find<Button>(view, "PlaybackButton").BringIntoView();
        Capture(window, "timer-controls.png");
        slider.Value = 3;
        Find<CheckBox>(view, "TimerEnabledToggle").IsChecked = false;
        Assert.False(vm.TimerEnabled);
        Assert.False(slider.IsEffectivelyEnabled);
        Assert.Equal(L10n.Get("Session.NoTimer"), Find<TextBlock>(view, "TimerReadoutText").Text);
        await vm.TogglePlaybackAsync();
        await vm.TickAsync();
        Assert.True(vm.IsRunning);
        Assert.False(vm.IsTimedSession);
        Assert.False(progress.IsVisible);
        Assert.Equal(L10n.Get("Session.NoTimer"), vm.Remaining);
        await vm.StopAsync();
        await vm.SaveAsync();
        var mode = Assert.Single((await new WorkspaceStore(folder.Path).LoadAsync()).Modes!);
        Assert.False(mode.TimerEnabled);
        Assert.Equal(3, mode.DurationSeconds);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TimerLimitInSettingsClampsSliderAndPersistsWithoutChangingOutputOrNames()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 760, Height = 900 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        vm.ChannelA = 20;
        vm.EditorName = "Timer settings";
        var selectedDevice = vm.SelectedDevice;
        var slider = Find<Slider>(view, "DurationSlider");
        Assert.Equal(600, slider.Value);
        slider.Value = 1800;
        await vm.NavigateAsync("settings");
        Find<NumericUpDown>(view, "TimerLimitInput").Value = 5;
        Assert.Equal(300, slider.Maximum);
        Assert.Equal(300, vm.SessionDurationSeconds);
        await vm.SaveSettingsAsync();
        await vm.NavigateAsync("workspace");
        slider.BringIntoView();
        slider.Focus();
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Equal(299, slider.Value);
        Assert.Equal("04:59", Find<TextBlock>(view, "TimerReadoutText").Text);
        var timerRow = Find<Grid>(view, "TimerRow");
        var toggle = Find<CheckBox>(view, "TimerEnabledToggle");
        var readout = Find<TextBlock>(view, "TimerReadoutText");
        var toggleCenter = toggle.TranslatePoint(new Point(0, toggle.Bounds.Height / 2), timerRow)!.Value.Y;
        var sliderCenter = slider.TranslatePoint(new Point(0, slider.Bounds.Height / 2), timerRow)!.Value.Y;
        var readoutCenter = readout.TranslatePoint(new Point(0, readout.Bounds.Height / 2), timerRow)!.Value.Y;
        Assert.Equal(toggleCenter, sliderCenter, 1);
        Assert.Equal(sliderCenter, readoutCenter, 1);
        Assert.True(slider.Bounds.Width > 100);
        Find<Button>(view, "PlaybackButton").BringIntoView();
        Capture(window, "timer-slider-compact.png");
        Assert.Same(selectedDevice, vm.SelectedDevice);
        Assert.Equal(20, vm.ChannelA);
        Assert.Equal("Timer settings", vm.EditorName);
        await vm.SaveAsync();
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(5, saved.Settings.TimerLimitMinutes);
        Assert.Equal(299, Assert.Single(saved.Modes!).DurationSeconds);
        await using var restored = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        await restored.InitializeAsync();
        Assert.Equal(5, restored.TimerLimitMinutes);
        Assert.Equal(299, restored.SessionDurationSeconds);
        window.Close();
    }

    [AvaloniaFact]
    public async Task IncompatibleLiveSelectionRestoresTheCurrentWaveform()
    {
        using var folder = new TestFolder();
        var adapter = new FakeBluetoothAdapter();
        await new WorkspaceStore(folder.Path).SaveAsync(new(2, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 600 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 1 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        vm.ChannelA = 10;
        await vm.TogglePlaybackAsync();
        var original = vm.SelectedPreset;
        Find<ComboBox>(view, "PresetPicker").SelectedItem = vm.Presets.First(preset => preset.Preset.Family == WaveformFamily.Motor);
        await WaitForAsync(() => vm.IsError);
        Assert.Same(original, vm.SelectedPreset);
        Assert.Same(original, Find<ComboBox>(view, "PresetPicker").SelectedItem);
        Assert.True(vm.IsRunning);
        Assert.Empty(vm.Sessions);
        await vm.StopAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task BluetoothSourceSwitchPersistsChoiceUpdatesTelemetryAndResetsOutput()
    {
        using var folder = new TestFolder();
        var adapter = new FakeBluetoothAdapter();
        var simulation = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(simulation), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 600 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        vm.ChannelA = 30;
        await vm.NavigateAsync("settings");
        Find<ComboBox>(view, "TransportPicker").SelectedItem = vm.TransportOptions.Single(option => option.UseBluetooth);
        Click(window, Find<Button>(view, "SaveSettingsButton"));
        await WaitForAsync(() => vm.IsBluetooth && vm.Devices.Count == 1 && !vm.IsScanning);
        Assert.Empty(simulation.Outputs);
        Assert.Equal(30, vm.ChannelA);
        Assert.Equal("YCY-DJ", vm.SelectedDevice!.Name);
        Assert.Equal("—", vm.SelectedBattery);
        Assert.True((await new WorkspaceStore(folder.Path).LoadAsync()).Settings.UseBluetooth);
        vm.SelectedMode.AssignTarget("sim-dual-01", false);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        await WaitForAsync(() => vm.SelectedBattery == "82%");
        var link = adapter.Links[vm.SelectedDevice.Id];
        await Task.Run(() => link.Notify(V1Protocol.WithChecksum([0x35, 0x71, 4, 42])), TestContext.Current.CancellationToken);
        await WaitForAsync(() => vm.SelectedBattery == "42%");
        Assert.True(vm.HasElectrodeReadings);
        Assert.Contains(L10n.Get("Contact.NotConnected"), vm.SelectedContacts);
        await vm.NavigateAsync("workspace");
        Capture(window, "bluetooth-workspace.png");
        vm.ChannelA = 20;
        await vm.TogglePlaybackAsync();
        await vm.TickAsync();
        vm.SelectedTransport = vm.TransportOptions.Single(option => !option.UseBluetooth);
        await vm.SaveSettingsAsync();
        Assert.False(vm.IsRunning);
        Assert.False(vm.IsBluetooth);
        Assert.Equal(20, vm.ChannelA);
        Assert.Equal(5, vm.Devices.Count);
        Assert.True(link.Closed);
        Assert.Equal(V2Protocol.Stop(), link.Writes[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LostBluetoothConnectionStopsOtherLinkedDevices()
    {
        using var folder = new TestFolder();
        var adapter = new FakeBluetoothAdapter();
        adapter.Devices.Add(new("ble:000000000002", "YCY-DJ", -60));
        await new WorkspaceStore(folder.Path).SaveAsync(new(1, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 600 };
        window.Show();
        await WaitForAsync(() => vm.IsBluetooth && vm.Devices.Count == 2 && !vm.IsScanning);
        Assert.Equal(0, vm.ConnectedCount);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        await vm.ToggleConnectionAsync(vm.Devices[1]);
        vm.SelectedMode.AssignTarget(vm.Devices[1].Id, true); vm.ChannelA = 20;
        await vm.TogglePlaybackAsync();
        await vm.TickAsync();
        var lost = adapter.Links[vm.Devices[0].Id];
        await Task.Run(lost.LoseConnection, TestContext.Current.CancellationToken);
        await WaitForAsync(() => !vm.IsRunning && !vm.Devices[0].IsConnected && vm.IsError);
        Assert.True(lost.Closed);
        Assert.Equal(V2Protocol.Stop(), adapter.Links[vm.Devices[1].Id].Writes[^1]);
        Assert.Equal("Reason.ConnectionError", Assert.Single(vm.Sessions).Record.Reason);
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartupAndEmptySelectionsProduceNoBindingErrorsOrValidationExceptions()
    {
        using var folder = new TestFolder();
        var previousSink = Logger.Sink;
        var sink = new BindingErrorSink();
        var validationErrors = new ConcurrentQueue<Exception>();
        void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is LocalizedArgumentException) validationErrors.Enqueue(e.Exception);
        }
        Logger.Sink = sink;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        Window? window = null;
        try
        {
            var controller = new DeviceController(new SimulationTransport());
            await using var vm = new MainViewModel(controller, new WorkspaceStore(folder.Path));
            Assert.Null(vm.SelectedDevice);
            Assert.Null(vm.SelectedPreset);
            var view = new MainView { DataContext = vm };
            Assert.Equal(L10n.Format("Device.Output", 0), Find<TextBlock>(view, "SelectedOutputAText").Text);
            Assert.Equal(L10n.Format("Device.Output", 0), Find<TextBlock>(view, "SelectedOutputBText").Text);
            window = new Window { Content = view, Width = 1000, Height = 600 };
            window.Show();
            await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
            var selected = vm.SelectedPreset;
            vm.SelectedPreset = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(Find<WaveformPlot>(view, "MainPlot").Preset);
            Assert.Equal(L10n.Get("Wave.Select"), Find<TextBlock>(view, "SelectedWaveNameText").Text);
            vm.SelectedPreset = selected;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(selected!.Preset, Find<WaveformPlot>(view, "MainPlot").Preset);
            Assert.Equal(selected.Name, Find<TextBlock>(view, "SelectedWaveNameText").Text);
            Assert.Null(vm.EditorPreview);
            vm.EditorName = "Draft";
            Assert.NotNull(vm.EditorPreview);
            vm.EditorPeriod = 0.1;
            Assert.Null(vm.EditorPreview);
            vm.EditorPeriod = 4;
            vm.EditorShape = WaveShape.Sampled;
            Assert.Null(vm.EditorPreview);
            await vm.ToggleConnectionAsync(vm.Devices[0]);
            vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
            await vm.ToggleConnectionAsync(vm.Devices[1]);
            await vm.SelectDeviceAsync(vm.Devices[0]);
            await controller.SendAsync(vm.SelectedDevice!.Id, new(25, 15, 20), TestContext.Current.CancellationToken);
            Assert.Equal(25, vm.SelectedDevice.OutputA);
            Assert.Equal(vm.SelectedOutputALabel, Find<TextBlock>(view, "SelectedOutputAText").Text);
            Assert.Equal(vm.SelectedOutputBLabel, Find<TextBlock>(view, "SelectedOutputBText").Text);
            await vm.SelectDeviceAsync(vm.Devices[1]);
            Assert.Equal(L10n.Format("Device.Output", 0), Find<TextBlock>(view, "SelectedOutputAText").Text);
            Assert.True(sink.Errors.IsEmpty && validationErrors.IsEmpty,
                $"Binding errors: {string.Join("; ", sink.Errors)}. Validation exceptions: {validationErrors.Count}.");
        }
        finally
        {
            window?.Close();
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            Logger.Sink = previousSink;
        }
    }

    [AvaloniaFact]
    public async Task SplitViewsProtectDefaultsAndPreserveSamplesWhenDuplicating()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 600 };
        window.Show();
        await WaitForAsync(() => vm.Presets.Count == 22 && !vm.IsScanning);
        Assert.NotNull(Find<DeviceWorkspaceView>(view, "WorkspacePage"));
        Assert.NotNull(Find<WaveformLibraryView>(view, "WavesPage"));
        Assert.NotNull(Find<SessionHistoryView>(view, "HistoryPage"));
        Assert.NotNull(Find<SettingsView>(view, "SettingsPage"));
        Assert.Single(view.GetLogicalDescendants().OfType<WaveformEditorView>());
        Assert.Single(view.GetLogicalDescendants().OfType<WaveformTransferView>());
        await vm.NavigateAsync("waves");
        var original = vm.LibrarySelectedPreset!.Preset;
        Assert.False(vm.DeleteWaveCommand.CanExecute(null));
        Assert.False(Find<Button>(view, "DeleteWaveButton").IsEffectivelyEnabled);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(vm.DeleteWaveAsync);
        vm.EditWave();
        await vm.SaveWaveAsync();
        Assert.Equal(23, vm.Presets.Count);
        Assert.False(vm.LibrarySelectedPreset!.IsBuiltIn);
        Assert.Equivalent(original.Samples, vm.LibrarySelectedPreset.Preset.Samples);
        Assert.True(vm.DeleteWaveCommand.CanExecute(null));
        Assert.True(Find<Button>(view, "DeleteWaveButton").IsEffectivelyEnabled);
        Click(window, Find<Button>(view, "DeleteWaveButton"));
        await WaitForAsync(() => vm.Presets.Count == 22);
        Assert.False(vm.DeleteWaveCommand.CanExecute(null));
        Capture(window, "default-waveforms.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task LanguagePickerRefreshesExistingUiAndPersistsSelection()
    {
        using var folder = new TestFolder();
        var originalLanguage = L10n.Current.Language;
        try
        {
            await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
            var window = new MainWindow { DataContext = vm };
            window.Show();
            var view = Assert.IsType<MainView>(window.Content);
            await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
            await vm.ToggleConnectionAsync(vm.Devices[0]);
            vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
            vm.ChannelA = 20;
            await vm.TogglePlaybackAsync();
            await vm.StopAsync();
            var selectedDevice = vm.SelectedDevice;
            var selectedPreset = vm.SelectedPreset;
            vm.EditorName = "用户名称 / User name";

            await vm.NavigateAsync("settings");
            var picker = Find<ComboBox>(view, "LanguagePicker")!;
            picker.SelectedItem = vm.Languages.First(l => l.Code == "en");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Settings", Find<TextBlock>(view, "PageTitleText")!.Text);
            Assert.Equal("■  Stop all", Find<Button>(view, "StopAllButton")!.Content);
            Assert.Equal("YokoWave", window.Title);
            Assert.Same(L10n.Current.Culture.NumberFormat, Find<NumericUpDown>(view, "TimerLimitInput")!.NumberFormat);
            Assert.Equal("Tide", selectedPreset!.Name);
            Assert.Contains("Manual stop", vm.Sessions[0].Detail);
            vm.ShowError(new LocalizedArgumentException("Error.Period"));
            Assert.Contains("Period", vm.Status);
            Capture(window, "settings-en.png");

            picker.SelectedItem = vm.Languages.First(l => l.Code == "zh-CN");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("偏好设置", Find<TextBlock>(view, "PageTitleText")!.Text);
            Assert.Equal("■  全部停止", Find<Button>(view, "StopAllButton")!.Content);
            Assert.Equal("YokoWave · 波枢", window.Title);
            Assert.Same(L10n.Current.Culture.NumberFormat, Find<NumericUpDown>(view, "TimerLimitInput")!.NumberFormat);
            Assert.Equal("潮汐", selectedPreset.Name);
            Assert.Equal("正弦", vm.SelectedShape!.Label);
            Assert.Contains("周期", vm.Status);
            Assert.Contains("手动停止", vm.Sessions[0].Detail);
            Assert.Same(selectedDevice, vm.SelectedDevice);
            Assert.Same(selectedPreset, vm.SelectedPreset);
            Assert.Equal("用户名称 / User name", vm.EditorName);

            Assert.Equal(1, vm.ConnectedCount);
            Assert.Equal(20, vm.ChannelA);
            Capture(window, "settings-zh-CN.png");
            await vm.SaveAsync();
            Assert.Equal("zh-CN", (await new WorkspaceStore(folder.Path).LoadAsync()).Settings.Language);
            window.Close();
            await WaitForAsync(() => !window.IsVisible);

            L10n.Current.SetLanguage("en");
            await using var restored = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
            await restored.InitializeAsync();
            Assert.Equal("zh-CN", restored.SelectedLanguage!.Code);
            Assert.Equal("zh-CN", L10n.Current.Language);
        }
        finally { L10n.Current.SetLanguage(originalLanguage); }
    }

    [AvaloniaFact]
    public async Task NativeWindowShortcutAndCloseStopAndPersistTheSession()
    {
        using var folder = new TestFolder();
        var transport = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        vm.ChannelA = 20;
        await vm.TogglePlaybackAsync();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await WaitForAsync(() => !vm.IsRunning);
        Assert.All(transport.Outputs.Values, output => Assert.Equal(0, output.ChannelA));
        await vm.TogglePlaybackAsync();
        window.Close();
        await WaitForAsync(() => !window.IsVisible);
        Assert.Empty(transport.Outputs);
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(2, saved.Sessions.Count);
    }

    [AvaloniaFact]
    public async Task ThemeAndChartFollowLiveThemeAndAccentChanges()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var app = Application.Current!;
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1450, Height = 1100, RequestedThemeVariant = ThemeVariant.Light };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        vm.ChannelA = 40; vm.ChannelB = 25;
        Dispatcher.UIThread.RunJobs();
        var lightCanvas = Assert.IsAssignableFrom<ISolidColorBrush>(view.Background).Color;
        var plot = Find<WaveformPlot>(view, "MainPlot")!;
        var lightGrid = Assert.IsAssignableFrom<ISolidColorBrush>(plot.GridBrush).Color;
        Capture(window, "workspace-light.png");
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(lightCanvas, Assert.IsAssignableFrom<ISolidColorBrush>(view.Background).Color);
        Assert.NotEqual(lightGrid, Assert.IsAssignableFrom<ISolidColorBrush>(plot.GridBrush).Color);
        Capture(window, "workspace-dark.png");

        // Emulate the resource notifications that Fluent emits for an OS accent change.
        // Headless has no operating system preference service to manipulate.
        app.Resources["SystemAccentColor"] = Color.Parse("#A84078");
        app.Resources["SystemAccentColorLight2"] = Color.Parse("#EC90C1");
        Dispatcher.UIThread.RunJobs();
        var primary = Find<Button>(view, "PlaybackButton")!;
        var before = Assert.IsAssignableFrom<ISolidColorBrush>(primary.Background).Color;
        var chartBefore = Assert.IsAssignableFrom<ISolidColorBrush>(plot.ChannelABrush).Color;
        app.Resources["SystemAccentColor"] = Color.Parse("#227D69");
        app.Resources["SystemAccentColorLight2"] = Color.Parse("#6BDAB5");
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(before, Assert.IsAssignableFrom<ISolidColorBrush>(primary.Background).Color);
        Assert.NotEqual(chartBefore, Assert.IsAssignableFrom<ISolidColorBrush>(plot.ChannelABrush).Color);
        Assert.NotEqual(default, Assert.IsAssignableFrom<ISolidColorBrush>(primary.Foreground).Color);
        Capture(window, "workspace-custom-accent.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task WorkspaceButtonsSlidersStopAndNavigationWork()
    {
        using var folder = new TestFolder();
        var transport = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1450, Height = 1100 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        Assert.Equal(600, vm.SessionDurationSeconds);
        Dispatcher.UIThread.RunJobs();
        var connect = view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == L10n.Get("Device.Connect"));
        Click(window, connect);
        await WaitForAsync(() => vm.ConnectedCount == 1);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        var slider = Find<Slider>(view, "ChannelASlider")!;
        slider.Value = 35;
        Find<Slider>(view, "ChannelBSlider")!.Value = 20;
        Assert.Equal(35, vm.ChannelA);
        Click(window, Find<Button>(view, "PlaybackButton")!);
        await WaitForAsync(() => vm.IsRunning);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await vm.TickAsync();
        Assert.True(transport.Outputs["sim-dual-01"].ChannelA > 0);
        Capture(window, "workspace.png");

        Click(window, Find<Button>(view, "StopAllButton")!);
        await WaitForAsync(() => !vm.IsRunning);
        Assert.All(transport.Outputs.Values, output => Assert.Equal(0, output.ChannelA));
        Assert.Single(vm.Sessions);

        await vm.NavigateAsync("waves");
        vm.EditorName = "自定义波形";
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(Find<WaveformPlot>(view, "EditorPreviewPlot")!.Preset);
        vm.EditorShape = WaveShape.Custom;
        vm.EditorPoints = "0, 0.4, 1, 0.5, 0";
        Click(window, Find<Button>(view, "SaveWaveButton")!);
        await WaitForAsync(() => vm.Presets.Count == WaveformPreset.BuiltIn.Count + 1);
        Assert.Equal("自定义波形", vm.LibrarySelectedPreset!.Name);
        Capture(window, "waveforms.png");
        window.Close();
    }

    [AvaloniaFact]
    public async Task CompactLayoutAndPersistenceWorkflowWork()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 760, Height = 1040 };
        window.Show();
        await WaitForAsync(() => vm.Devices.Count == 5 && !vm.IsScanning);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Find<Border>(view, "Sidebar").IsVisible);
        Assert.True(Find<StackPanel>(view, "CompactNav").IsVisible);
        await vm.ToggleConnectionAsync(vm.Devices[0]);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        await vm.ToggleConnectionAsync(vm.Devices[1]);
        vm.SelectedMode.AssignTarget(vm.Devices[1].Id, true); vm.ChannelA = 25; vm.ChannelB = 15;
        await vm.TogglePlaybackAsync();
        await vm.NavigateAsync("history");
        Assert.True(vm.IsRunning);
        await vm.StopAsync();
        Assert.False(vm.IsRunning);
        Assert.Equal(2, Assert.Single(vm.Sessions).Record.DeviceCount);
        Capture(window, "compact-history.png");
        await vm.NavigateAsync("waves");
        await vm.ImportAsync(WorkspaceStore.ExportPresets([WaveformPreset.BuiltIn[0] with { Id = Guid.NewGuid(), Name = "导入测试" }]));
        await vm.ImportAsync(WorkspaceStore.ExportPresets(vm.Presets.Select(p => p.Preset)));
        Assert.Equal(WaveformPreset.BuiltIn.Count + 1, vm.Presets.Count);
        await vm.SaveAsync();
        var loaded = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Single(loaded.Presets);
        Assert.Single(loaded.Sessions);
        window.Close();
    }

    private sealed class BindingErrorSink : ILogSink
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area)) Errors.Enqueue(messageTemplate);
        }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Log(level, area, source, $"{messageTemplate} [{string.Join(", ", propertyValues)}]");
    }

    private static T Find<T>(Control root, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs();
        return root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    }

    private static void Click(Window window, Button button)
    {
        button.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "UI operation did not complete within 5 seconds.");
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
}
