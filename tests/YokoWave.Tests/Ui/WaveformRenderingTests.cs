using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using YokoWave.App.Controls;
using YokoWave.Core;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class WaveformRenderingTests
{
    [AvaloniaFact]
    public void CachedWaveformRenderingUpdatesForPlayheadAmplitudeWaveformSizeAndBrushes()
    {
        using var state = new UiTestState();
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
        var window = new Window
        {
            Content = plot,
            Width = 400,
            Height = 200
        };
        window.Show();
        try
        {
            byte[] initial = UiArtifacts.RenderPng(window);
            plot.Playhead = .25;
            byte[] playhead = UiArtifacts.RenderPng(window);
            Assert.False(initial.AsSpan().SequenceEqual(playhead));
            plot.AmplitudeA = 20;
            byte[] amplitude = UiArtifacts.RenderPng(window);
            Assert.False(playhead.AsSpan().SequenceEqual(amplitude));
            plot.Waveform = plot.Waveform with
            {
                Shape = WaveShape.Triangle
            };
            byte[] waveform = UiArtifacts.RenderPng(window);
            Assert.False(amplitude.AsSpan().SequenceEqual(waveform));
            plot.ChannelABrush = Brushes.Purple;
            byte[] color = UiArtifacts.RenderPng(window);
            Assert.False(waveform.AsSpan().SequenceEqual(color));
            window.Width = 450;
            Assert.False(color.AsSpan().SequenceEqual(UiArtifacts.RenderPng(window)));
        }
        finally
        {
            window.Close();
        }
    }
}
