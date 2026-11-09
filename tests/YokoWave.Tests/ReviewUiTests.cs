using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;
using YokoWave.App.Controls;
using YokoWave.App.ViewModels;
using YokoWave.Core;

namespace YokoWave.Tests;

public sealed class ReviewUiTests
{
    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(3);

    [AvaloniaFact]
    public async Task CommandsDispatchToUiRejectReentryAndRecoverWhenErrorReportingOrSubscribersThrow()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        int notifications = 0;
        var command = new AsyncCommand(async () =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            calls++;
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("execution");
        }, _ =>
                {
                    reported.TrySetResult();
                    throw new InvalidOperationException("reporting");
                });
        command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("subscriber");
        command.CanExecuteChanged += (_, _) =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            notifications++;
        };
        await Task.Run(() => command.Execute(null), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(1, calls);
        release.TrySetResult();
        await reported.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        Assert.True(command.CanExecute(null));
        Assert.Equal(2, notifications);
    }

    [AvaloniaFact]
    public void CachedWaveformRenderingUpdatesForPlayheadAmplitudePresetSizeAndBrushes()
    {
        var plot = new WaveformPlot
        {
            Waveform = new(Guid.NewGuid(), "sine", WaveShape.Sine, 4, 20),
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
            byte[] initial = Capture(window);
            plot.Playhead = .25;
            byte[] playhead = Capture(window);
            Assert.False(initial.AsSpan().SequenceEqual(playhead));
            plot.AmplitudeA = 20;
            byte[] amplitude = Capture(window);
            Assert.False(playhead.AsSpan().SequenceEqual(amplitude));
            plot.Waveform = plot.Waveform with
            {
                Shape = WaveShape.Triangle
            };
            byte[] waveform = Capture(window);
            Assert.False(amplitude.AsSpan().SequenceEqual(waveform));
            plot.ChannelABrush = Brushes.Purple;
            byte[] color = Capture(window);
            Assert.False(waveform.AsSpan().SequenceEqual(color));
            window.Width = 450;
            Assert.False(color.AsSpan().SequenceEqual(Capture(window)));
        }
        finally
        {
            window.Close();
        }
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
