using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using YokoWave.App.Controls;
using YokoWave.App.ViewModels;
using YokoWave.Core;
using Xunit;

namespace YokoWave.Tests;

public sealed class ReviewUiTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [AvaloniaFact]
    public async Task TickSchedulesIndependentModesWithoutWaitingForASlowDevice()
    {
        using var folder = new TestFolder();
        var transport = new ConcurrentTransport();
        await using var vm = new MainViewModel(new DeviceController(transport), new WorkspaceStore(folder.Path));
        await vm.InitializeAsync();
        foreach (var device in vm.Devices)
            await vm.ToggleConnectionAsync(device);
        var first = vm.SelectedMode;
        first.AssignTarget("slow", true);
        first.ChannelA = 20;
        await vm.AddModeAsync(ModeKind.Waveform);
        var second = vm.SelectedMode;
        second.AssignTarget("fast", true);
        second.ChannelA = 30;
        var preset = new PresetItem(new(Guid.NewGuid(), "constant", WaveShape.Custom, 4, 20, [1, 1]));
        vm.Presets.Add(preset);
        first.SelectedPreset = second.SelectedPreset = preset;
        await first.TogglePlaybackAsync();
        await second.TogglePlaybackAsync();
        transport.BlockedId = "slow";
        var tick = vm.TickAsync();
        try
        {
            await transport.FastOutput.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            Assert.True(first.IsRunning);
            Assert.True(second.IsRunning);
            Assert.Equal(30, transport.Outputs["fast"].ChannelA);
            Assert.False(tick.IsCompleted);
        }
        finally { transport.Release.TrySetResult(); await tick; }
        await vm.StopAllModesAsync();
    }

    [AvaloniaFact]
    public async Task CommandsDispatchToUiRejectReentryAndRecoverWhenErrorReportingOrSubscribersThrow()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var notifications = 0;
        var command = new AsyncCommand(async () =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            calls++;
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("execution");
        }, _ => { reported.TrySetResult(); throw new InvalidOperationException("reporting"); });
        command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("subscriber");
        command.CanExecuteChanged += (_, _) => { Assert.True(Dispatcher.UIThread.CheckAccess()); notifications++; };
        await Task.Run(() => command.Execute(null), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, calls);
        release.TrySetResult();
        await reported.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.True(command.CanExecute(null));
        Assert.Equal(2, notifications);
    }

    [AvaloniaFact]
    public void CachedWaveformRenderingUpdatesForPlayheadAmplitudePresetSizeAndBrushes()
    {
        var plot = new WaveformPlot
        {
            Preset = new(Guid.NewGuid(), "sine", WaveShape.Sine, 4, 20),
            ShowGrid = false,
            ChannelABrush = Brushes.Red,
            ChannelBBrush = Brushes.Blue,
            AreaBrush = Brushes.Transparent,
            PlayheadBrush = Brushes.Green,
            AmplitudeA = 70,
            AmplitudeB = 0
        };
        var window = new Window { Content = plot, Width = 400, Height = 200 };
        window.Show();
        try
        {
            var initial = Capture(window);
            plot.Playhead = .25;
            var playhead = Capture(window);
            Assert.False(initial.AsSpan().SequenceEqual(playhead));
            plot.AmplitudeA = 20;
            var amplitude = Capture(window);
            Assert.False(playhead.AsSpan().SequenceEqual(amplitude));
            plot.Preset = plot.Preset with
            {
                Shape = WaveShape.Triangle
            };
            var preset = Capture(window);
            Assert.False(amplitude.AsSpan().SequenceEqual(preset));
            plot.ChannelABrush = Brushes.Purple;
            var color = Capture(window);
            Assert.False(preset.AsSpan().SequenceEqual(color));
            window.Width = 450;
            Assert.False(color.AsSpan().SequenceEqual(Capture(window)));
        }
        finally { window.Close(); }
    }

    private static byte[] Capture(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var bytes = new MemoryStream();
        frame.Save(bytes, PngBitmapEncoderOptions.Default);
        return bytes.ToArray();
    }
}
