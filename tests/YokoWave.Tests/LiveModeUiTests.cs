using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.Concurrent;
using YokoWave.App.ViewModels;
using YokoWave.App.Views;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Core.Bluetooth;
using Xunit;

namespace YokoWave.Tests;

internal sealed class FakeAudioInputFactory(TimeProvider? clock = null) : IAudioInputFactory
{
    public bool SupportsSystemAudio => true;
    public List<AudioSettings> Requests { get; } = [];
    public List<FakeAudioInputSession> Sessions { get; } = [];
    public bool BlockStart { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<IAudioInputSession> StartAsync(AudioSettings settings, CancellationToken cancellationToken)
    {
        Requests.Add(settings); Entered.TrySetResult();
        if (BlockStart) await Task.Delay(Timeout.Infinite, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var session = new FakeAudioInputSession(clock ?? TimeProvider.System); Sessions.Add(session); return session;
    }
}

internal sealed class FakeAudioInputSession(TimeProvider clock) : IAudioInputSession
{
    private long _sequence;
    public bool Paused { get; private set; }
    public bool Disposed { get; private set; }
    public bool Ended { get; set; }
    public double Age { get; set; }
    public double Db { get; set; } = -6;
    public AudioFeatureFrame? Latest => Disposed || Paused ? null : new(++_sequence, clock.GetUtcNow().AddSeconds(-Age), Db, Db, 1, 1);
    public void SetPaused(bool paused) => Paused = paused;
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

public sealed class LiveModeUiTests
{
    [AvaloniaTheory]
    [InlineData(AudioSourceKind.Microphone)]
    [InlineData(AudioSourceKind.MediaFile)]
    [InlineData(AudioSourceKind.SystemAudio)]
    public async Task AudioSourcesPauseDisposeAndStopIndependently(AudioSourceKind source)
    {
        using var folder = new TestFolder(); var transport = new SimulationTransport(); var factory = new FakeAudioInputFactory();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path), audioInputFactory: factory);
        await vm.InitializeAsync();
        foreach (var device in vm.Devices.Take(2)) await vm.ToggleConnectionAsync(device);
        var wave = vm.SelectedMode; wave.AssignTarget(vm.Devices[0].Id, true); wave.ChannelA = 15;
        await wave.TogglePlaybackAsync();
        await vm.AddModeAsync(ModeKind.Audio); var audio = vm.SelectedMode;
        audio.AssignTarget(vm.Devices[1].Id, true);
        audio.SelectedAudioSource = audio.AudioSources.Single(option => option.Value == (int)source);
        audio.MediaPath = "local.wav"; audio.ChannelA = 20; audio.ChannelB = 0;
        await audio.TogglePlaybackAsync(); await vm.TickAsync();
        Assert.Equal(source, Assert.Single(factory.Requests).Source);
        var input = Assert.Single(factory.Sessions);
        Assert.True(transport.Outputs[vm.Devices[1].Id].ChannelA > 0); Assert.Equal(0, transport.Outputs[vm.Devices[1].Id].ChannelB);
        await audio.TogglePauseAsync(); Assert.True(input.Paused); Assert.Equal(0, transport.Outputs[vm.Devices[1].Id].ChannelA);
        Assert.Equal(15, transport.Outputs[vm.Devices[0].Id].ChannelA);
        await audio.TogglePauseAsync(); await vm.TickAsync(); Assert.False(input.Paused);
        input.Ended = true; await vm.TickAsync();
        Assert.True(input.Disposed); Assert.False(audio.IsRunning); Assert.True(wave.IsRunning);
        Assert.Equal("Reason.MediaEnded", Assert.Single(vm.Sessions).Record.Reason);
        Assert.Equal(0, transport.Outputs[vm.Devices[1].Id].ChannelA);
    }

    [AvaloniaFact]
    public async Task AudioDoesNotOpenWithoutTargetsAndEmergencyStopCancelsPendingCapture()
    {
        using var folder = new TestFolder(); var factory = new FakeAudioInputFactory { BlockStart = true };
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path), audioInputFactory: factory);
        await vm.InitializeAsync(); await vm.AddModeAsync(ModeKind.Audio);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(vm.TogglePlaybackAsync); Assert.Empty(factory.Requests);
        await vm.ToggleConnectionAsync(vm.Devices[0]); vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true);
        var start = vm.TogglePlaybackAsync(); await factory.Entered.Task;
        await vm.StopAllModesAsync(); await start;
        Assert.False(vm.IsRunning); Assert.True(vm.CanEditRouting); Assert.Empty(factory.Sessions);
    }

    [AvaloniaFact]
    public async Task LostAudioInputStopsOnlyItsModeAndReleasesCapture()
    {
        using var folder = new TestFolder(); var clock = new ManualInputClock(); var factory = new FakeAudioInputFactory(clock);
        var transport = new SimulationTransport();
        await using var vm = new MainViewModel(new DeviceController(transport, clock), new WorkspaceStore(folder.Path), audioInputFactory: factory);
        await vm.InitializeAsync(); await vm.ToggleConnectionAsync(vm.Devices[0]); await vm.AddModeAsync(ModeKind.Audio);
        vm.SelectedMode.AssignTarget(vm.Devices[0].Id, true); vm.ChannelA = 20;
        await vm.TogglePlaybackAsync(); await vm.TickAsync();
        var input = Assert.Single(factory.Sessions); input.Age = 3;
        await vm.TickAsync();
        Assert.False(vm.IsRunning); Assert.True(input.Disposed); Assert.True(vm.IsError);
        Assert.Equal("Reason.InputLost", Assert.Single(vm.Sessions).Record.Reason);
        Assert.Equal(0, transport.Outputs[vm.Devices[0].Id].ChannelA);
    }

    [AvaloniaFact]
    public async Task FailedAudioStopReportsTheUnconfirmedOutputWithoutAbortingOtherModes()
    {
        using var folder = new TestFolder(); var adapter = new FakeBluetoothAdapter(); var factory = new FakeAudioInputFactory();
        adapter.Devices.Add(new("ble:000000000002", "YCY-DJ", -55));
        await new WorkspaceStore(folder.Path).SaveAsync(new(5, [], [], new(UseBluetooth: true)));
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path),
            () => new ElectricalBluetoothTransport(adapter), factory);
        await vm.InitializeAsync();
        foreach (var device in vm.Devices) await vm.ToggleConnectionAsync(device);
        var wave = vm.SelectedMode; wave.AssignTarget(vm.Devices[0].Id, true); wave.ChannelA = 10; await wave.TogglePlaybackAsync();
        await vm.AddModeAsync(ModeKind.Audio); var audio = vm.SelectedMode;
        audio.AssignTarget(vm.Devices[1].Id, true); audio.ChannelA = 20; await audio.TogglePlaybackAsync(); await vm.TickAsync();
        var link = adapter.Links[vm.Devices[1].Id];
        try
        {
            link.FailWrites = true; factory.Sessions[0].Age = 3;
            await vm.TickAsync();
            Assert.False(audio.IsRunning); Assert.True(factory.Sessions[0].Disposed); Assert.True(vm.IsError);
            Assert.True(wave.IsRunning); Assert.Equal(10, vm.Devices[0].OutputA);
            Assert.False(audio.CanEditRouting);
        }
        finally { link.FailWrites = false; await vm.StopAllModesAsync(); }
    }

    [AvaloniaFact]
    public async Task AmbientCalibrationKeepsOutputsOffAndStoresOnlyTheNoiseFloor()
    {
        using var folder = new TestFolder(); var factory = new FakeAudioInputFactory();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path), audioInputFactory: factory);
        await vm.InitializeAsync(); await vm.AddModeAsync(ModeKind.Audio);
        var calibration = vm.SelectedMode.CalibrateAudioCommand;
        calibration.Execute(null);
        await Wait(() => factory.Sessions.Count == 1);
        factory.Sessions[0].Db = -55;
        Assert.True(vm.SelectedMode.IsCalibrating); Assert.False(vm.PlaybackCommand.CanExecute(null));
        await Wait(() => !vm.SelectedMode.IsCalibrating);
        Assert.Equal(-55, vm.SelectedMode.NoiseFloorDb); Assert.True(factory.Sessions[0].Disposed);
        Assert.False(vm.IsRunning); Assert.Empty(vm.Sessions);
        await vm.SaveAsync();
        var saved = await new WorkspaceStore(folder.Path).LoadAsync();
        Assert.Equal(-55, saved.Modes!.Single(mode => mode.Kind == ModeKind.Audio).Audio!.NoiseFloorDb);
    }

    [AvaloniaFact]
    public async Task NewModePanelsLocalizePersistAndDriveOnlyTheirOwnSimulatedDevice()
    {
        using var folder = new TestFolder(); var transport = new SimulationTransport();
        var previousSink = Logger.Sink; var sink = new Errors(); Logger.Sink = sink;
        var previousLanguage = L10n.Current.Language;
        Window? window = null;
        try
        {
            await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path), audioInputFactory: new FakeAudioInputFactory());
            var view = new MainView { DataContext = vm }; window = new Window { Content = view, Width = 1200, Height = 1000 };
            window.Show(); await Wait(() => vm.Devices.Count == 5 && !vm.IsScanning);
            foreach (var kind in Enum.GetValues<ModeKind>().Skip(1)) await vm.AddModeAsync(kind);
            Assert.Equal(6, vm.Modes.Count);
            var restraint = vm.Modes.Single(mode => mode.IsRestraint); vm.SelectedMode = restraint;
            restraint.Name = "Own baseline"; restraint.SafeAngle = 5; restraint.RampAngle = 30;
            await vm.ToggleConnectionAsync(vm.Devices[0]); restraint.AssignTarget(vm.Devices[0].Id, true); restraint.ChannelA = 20;
            await restraint.TogglePlaybackAsync(); await vm.TickAsync();
            restraint.SimulatedAngle = 45; await vm.TickAsync();
            Assert.Equal(20, transport.Outputs[vm.Devices[0].Id].ChannelA);
            Assert.True(view.GetVisualDescendants().OfType<RestraintModeView>().Single().IsVisible);
            Assert.False(view.GetVisualDescendants().OfType<AudioModeView>().Single().IsVisible);
            view.GetVisualDescendants().OfType<RestraintModeView>().Single().BringIntoView(); Capture(window, "restraint-mode.png");
            await restraint.StopAsync();
            var audio = vm.Modes.Single(mode => mode.IsAudio); vm.SelectedMode = audio;
            audio.SelectedAudioSource = audio.AudioSources.Single(option => option.Value == (int)AudioSourceKind.MediaFile);
            audio.MediaPath = "User audio.mp3"; audio.AudioSensitivity = 77;
            vm.SelectedLanguage = vm.Languages.Single(language => language.Code == "en");
            Assert.Equal("Own baseline", restraint.Name); Assert.Equal(77, audio.AudioSensitivity); Assert.Equal("User audio.mp3", audio.MediaPath);
            view.GetVisualDescendants().OfType<AudioModeView>().Single().BringIntoView(); Capture(window, "audio-mode-en.png");
            vm.SelectedMode = vm.Modes.Single(mode => mode.IsExercise);
            view.GetVisualDescendants().OfType<ExerciseModeView>().Single().BringIntoView(); Capture(window, "exercise-mode.png");
            var sleep = vm.Modes.Single(mode => mode.IsSleep); vm.SelectedMode = sleep;
            sleep.SelectedSleepCycle = sleep.SleepCycles.Single(option => option.Value == (int)SleepCycleKind.Random);
            window.RequestedThemeVariant = ThemeVariant.Dark;
            view.GetVisualDescendants().OfType<SleepModeView>().Single().BringIntoView(); Capture(window, "sleep-random-mode.png");
            window.Width = 600; Dispatcher.UIThread.RunJobs();
            view.GetVisualDescendants().OfType<SleepModeView>().Single().BringIntoView(); Capture(window, "sleep-mode-compact.png");
            await vm.SaveAsync();
            var saved = await new WorkspaceStore(folder.Path).LoadAsync();
            Assert.Equal(Enum.GetValues<ModeKind>().Order(), saved.Modes!.Select(mode => mode.Kind).Order());
            Assert.Equal(77, saved.Modes!.Single(mode => mode.Kind == ModeKind.Audio).Audio!.Sensitivity);
            Assert.Empty(sink.Messages);
        }
        finally { window?.Close(); Logger.Sink = previousSink; L10n.Current.SetLanguage(previousLanguage); }
    }
    private static async Task Wait(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            Dispatcher.UIThread.RunJobs(); if (condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
    private static void Capture(Window window, string file)
    {
        Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
        Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, file), PngBitmapEncoderOptions.Default);
    }
    private sealed class Errors : ILogSink
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) { if (IsEnabled(level, area)) Messages.Enqueue(messageTemplate); }
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values) => Log(level, area, source, messageTemplate);
    }
}
