using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify.Avalonia;
using Xunit;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views.Pipelines;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;

namespace YokoWave.Tests;

public sealed class WaveformChannelTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentSourcePortsPreserveChannelValuesAndZeroUndefinedChannelsDuringLiveChanges(bool playlist)
    {
        var multi = Multi();
        var mono = Mono();
        var builder = new PipelineBuilder("channels");
        var wave = playlist ? builder.Add(BuiltInNodes.Playlist, new PlaylistNode(multi.Id)) : builder.Add(BuiltInNodes.Waveform, new WaveformNode(multi.Id));
        for (int channel = 0; channel < 3; channel++)
        {
            var sink = builder.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode("out", channel, Limit: 1));
            builder.Connect(wave, WaveformPorts.Level(channel), sink, "strength");
        }
        var runtime = new PipelineRuntime(new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(builder.Build(), [multi, mono]), new HashSet<string> { "out" });
        var now = DateTimeOffset.UtcNow;
        var output = runtime.Evaluate(TimeSpan.Zero, now).Outputs["out"];
        Assert.Equal(80, output.ChannelA);
        Assert.Equal(50, output.ChannelB);
        Assert.Equal(25, output.ChannelC);
        Assert.True(runtime.Update(NodeControl.Create(wave.Id, "waveform", mono)));
        var single = runtime.Evaluate(TimeSpan.FromSeconds(.1), now.AddSeconds(.1));
        Assert.Equal(100, single.Outputs["out"].ChannelA);
        Assert.Equal(0, single.Outputs["out"].ChannelB);
        Assert.Equal(0, single.Outputs["out"].ChannelC);
        Assert.Empty(single.Diagnostics);
        Assert.True(runtime.Update(NodeControl.Create(wave.Id, "waveform", multi)));
        var restored = runtime.Evaluate(TimeSpan.FromSeconds(.2), now.AddSeconds(.2)).Outputs["out"];
        Assert.Equal(output.ChannelA, restored.ChannelA);
        Assert.Equal(output.ChannelB, restored.ChannelB);
        Assert.Equal(output.ChannelC, restored.ChannelC);
    }

    [Fact]
    public void ChannelDefinitionsComeFromActualSamplesAndGeneratedWavesHaveOneSourceChannel()
    {
        Assert.Equal(3, Multi().ChannelCount);
        Assert.Equal(1, Mono().ChannelCount);
        Assert.Equal(1, new WaveformAsset(Guid.NewGuid(), "Sine", WaveShape.Sine, 1, 20).ChannelCount);
        var variable = Multi() with { Samples = [new(1, 20), new(1, 20, Channels: [1, .5])] };
        Assert.Equal(2, variable.ChannelCount);
        Assert.Equal(10, DefaultWaveformCatalog.Waveforms.Count(wave => wave.ChannelCount == 3));
    }

    [AvaloniaFact]
    public async Task SourceConnectorsFollowTheCurrentWaveAndMetersShowOnlySuccessfullySentLimitedStrength()
    {
        using var folder = new TestFolder();
        var transport = new RecordingTransport();
        var controller = new DeviceController(transport);
        await using var vm = new MainViewModel(controller, new(folder.Path));
        await vm.InitializeAsync();
        var multi = Multi();
        var mono = Mono();
        await vm.Waveforms.AddAsync(multi);
        await vm.Waveforms.AddAsync(mono);
        var device = new DeviceDescriptor("meters", "Meters", DeviceKind.DualChannel, 80, -40, ProtocolId: nameof(BluetoothProtocol.CoyoteV3), Simulated: true);
        controller.LoadOutputSettings([new(device.Id, Hardware: new(25, 80))]);
        await controller.ConnectAsync(device, TestContext.Current.CancellationToken);
        var builder = new PipelineBuilder("Live output");
        var wave = builder.Add(BuiltInNodes.Waveform, new WaveformNode(multi.Id));
        for (int channel = 0; channel < 2; channel++)
        {
            var sink = builder.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(device.Id, channel, Limit: .6));
            builder.Connect(wave, WaveformPorts.Level(channel), sink, "strength");
        }
        vm.Pipelines.AddDocument(builder.Build());
        var editor = vm.Pipelines.Selected!;
        var source = Assert.Single(editor.Nodes.OfType<WaveformEditorNodeViewModel>());
        var sinks = editor.Nodes.OfType<ChannelOutputEditorViewModel>().OrderBy(n => n.Channel).ToArray();
        var view = new PipelineEditorView { DataContext = editor };
        var window = new Window { Width = 1250, Height = 750, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            editor.SelectedNodes.Add(source);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, source.Outputs.Count(p => p.IsAvailable));
            var picker = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
            Assert.All(sinks, sink => Assert.Equal(0, Assert.Single(sink.SentStrength).Value));
            await editor.StartAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(picker.IsEffectivelyEnabled);
            Assert.Equal(25, Assert.Single(sinks[0].SentStrength).Value);
            Assert.Equal(50, Assert.Single(sinks[1].SentStrength).Value);
            Assert.All(view.GetVisualDescendants().OfType<SentStrengthView>(), meter =>
            {
                var value = Assert.IsType<SentStrengthViewModel>(meter.DataContext);
                Assert.All(meter.GetVisualDescendants().OfType<TextBlock>(), text =>
                {
                    if (text.IsEffectivelyVisible)
                        Assert.Equal(value.ChannelName, text.Text);
                });
            });
            var savedRoutes = editor.Connections.ToArray();
            picker.SelectedItem = editor.Waveforms.Single(p => p.Asset.Id == mono.Id);
            await editor.Session.FlushAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, source.Outputs.Count(p => p.IsAvailable));
            Assert.Equal(25, Assert.Single(sinks[0].SentStrength).Value);
            Assert.Equal(0, Assert.Single(sinks[1].SentStrength).Value);
            Assert.Equal(savedRoutes, editor.Connections.ToArray());
            var hiddenRoute = editor.Connections.Single(c => c.Source.Port.Id == "levelB");
            Assert.False(hiddenRoute.Source.IsAvailable);
            picker.SelectedItem = editor.Waveforms.Single(p => p.Asset.Id == multi.Id);
            await editor.Session.FlushAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(hiddenRoute.Source.IsAvailable);
            Assert.Equal(50, Assert.Single(sinks[1].SentStrength).Value);
            view.GetVisualDescendants().OfType<NodifyEditor>().Single().FitToScreen();
            Dispatcher.UIThread.RunJobs();
            using var image = window.CaptureRenderedFrame();
            Assert.NotNull(image);
            string screenshots = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
            Directory.CreateDirectory(screenshots);
            image.Save(Path.Combine(screenshots, "waveform-channels-and-sent-strength.png"), PngBitmapEncoderOptions.Default);
            await editor.StopAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.All(sinks, sink => Assert.Equal(0, Assert.Single(sink.SentStrength).Value));
            transport.FailId = device.Id;
            await Assert.ThrowsAnyAsync<Exception>(() => editor.StartAsync());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PipelineState.Faulted, editor.Session.State);
            Assert.All(sinks, sink => Assert.True(Assert.Single(sink.SentStrength).IsUnknown));
        }
        finally
        {
            transport.FailId = null;
            await editor.StopAsync();
            window.Close();
        }
    }
    private static WaveformAsset Multi() => new(Guid.NewGuid(), "Three channels", WaveShape.Sampled, .2, 20,
        Samples: [new(1, 20, .8, [-.8, .5, .25]), new(1, 20, .8, [-.8, .5, .25])]);
    private static WaveformAsset Mono() => new(Guid.NewGuid(), "One channel", WaveShape.Custom, .2, 20, [1, 1]);
}
