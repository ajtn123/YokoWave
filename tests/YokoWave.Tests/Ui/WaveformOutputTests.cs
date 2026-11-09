using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify.Avalonia;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views.Pipelines;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;
using YokoWave.Tests.Support.Ui;
using static YokoWave.Tests.Support.Pipelines.WaveformTestAssets;

namespace YokoWave.Tests.Ui;

public sealed class WaveformOutputTests
{
    [AvaloniaFact]
    public async Task SourceConnectorsFollowTheCurrentWaveAndMetersShowOnlySuccessfullySentLimitedStrength()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        var transport = new RecordingTransport();
        var controller = new DeviceController(transport);
        await using var vm = new MainViewModel(controller, new(folder.Path));
        await vm.InitializeAsync();
        var multi = CreateThreeChannelWaveform();
        var mono = CreateSingleChannelWaveform();
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
        var view = new PipelineEditorView
        {
            DataContext = editor
        };
        var window = new Window
        {
            Width = 1250,
            Height = 750,
            Content = view
        };
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
            UiArtifacts.Capture(window, "waveform-channels-and-sent-strength.png");
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
}
