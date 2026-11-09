using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YokoWave.App.ViewModels;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.App.Views;
using YokoWave.App.Views.Components;
using YokoWave.App.Views.Pipelines;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class UiConsistencyTests
{
    [AvaloniaFact]
    public async Task SharedCollectionActionsFitInspectorsAndPreserveEditingAcrossLanguagesAndThemes()
    {
        await using var controller = new DeviceController(new SimulationTransport());
        var waveforms = WaveformPreset.BuiltIn.Take(2).Select(w => new PresetItem(w)).ToArray();
        var builder = new PipelineBuilder("collection editors");
        builder.Add(BuiltInNodes.Playlist, new PlaylistNode(waveforms[0].Preset.Id, waveforms.Select(w => w.Preset.Id).ToArray()));
        await using var editor = new PipelineEditorViewModel(builder.Build(), controller, new(waveforms), _ => { }, _ => { });
        var playlist = editor.Nodes.OfType<PlaylistEditorViewModel>().Single();
        var frequency = new FrequencySequenceEditorViewModel([new(10), new(30)], () => true, () => { });
        var samples = new WaveformSamplesEditorViewModel([new(.2, 10), new(.8, 30)], () => { });
        var frequencyView = new FrequencySequenceEditorView { DataContext = frequency };
        var samplesView = new WaveformSamplesEditorView { DataContext = samples };
        var inspector = new NodeInspectorView { DataContext = playlist };
        var content = new StackPanel { Width = 188, Children = { frequencyView, samplesView, inspector } };
        var window = new Window { Width = 230, Height = 1300, Content = content };
        var language = L10n.Current.Language;
        window.Show();
        try
        {
            Button[] Actions(Control view) => view.GetVisualDescendants().OfType<CollectionActionsView>().Single()
                .GetVisualDescendants().OfType<Button>().ToArray();
            Dispatcher.UIThread.RunJobs();
            var frequencyActions = Actions(frequencyView);
            Assert.False(frequencyActions[1].IsEffectivelyEnabled);
            Click(window, frequencyActions[0]);
            Assert.Equal(3, frequency.Rows.Count);
            var addedFrequency = frequency.Selected!;
            addedFrequency.FrequencyHz = 66;
            Assert.True(frequencyActions[2].IsEffectivelyEnabled);
            Assert.False(frequencyActions[3].IsEffectivelyEnabled);
            Click(window, frequencyActions[2]);
            Assert.Equal(1, frequency.Rows.IndexOf(addedFrequency));
            Click(window, frequencyActions[1]);
            Assert.Equal(2, frequency.Rows.Count);
            Click(window, frequencyActions[1]);
            Assert.Single(frequency.Rows);
            Assert.False(frequencyActions[1].IsEffectivelyEnabled);

            var sampleActions = Actions(samplesView);
            Assert.False(sampleActions[1].IsEffectivelyEnabled);
            Click(window, sampleActions[0]);
            Assert.Equal(3, samples.Rows.Count);
            var addedSample = samples.Selected!;
            Click(window, sampleActions[2]);
            Assert.Equal(1, samples.Rows.IndexOf(addedSample));
            Click(window, sampleActions[1]);
            Assert.Equal(2, samples.Rows.Count);
            Assert.False(sampleActions[1].IsEffectivelyEnabled);

            var playlistActions = Actions(inspector);
            Assert.False(playlistActions[0].IsEffectivelyEnabled);
            Assert.False(playlistActions[1].IsEffectivelyEnabled);
            playlist.SelectedEntry = playlist.Playlist[1];
            Assert.True(playlistActions[2].IsEffectivelyEnabled);
            Assert.False(playlistActions[3].IsEffectivelyEnabled);
            Click(window, playlistActions[2]);
            Assert.Equal(waveforms[1], playlist.Playlist[0]);
            Click(window, playlistActions[1]);
            Assert.Single(playlist.Playlist);
            playlist.SelectedPreset = waveforms[1];
            Assert.True(playlistActions[0].IsEffectivelyEnabled);
            Click(window, playlistActions[0]);
            Assert.Equal(2, playlist.Playlist.Count);
            var before = editor.ToDocument().Nodes.Single().Configuration.GetRawText();

            foreach (var locale in new[] { "en", "zh-CN" })
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                L10n.Current.SetLanguage(locale);
                editor.RefreshLocalization();
                window.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();
                foreach (var row in content.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("collection-actions")))
                {
                    var buttons = row.Children.OfType<Button>().ToArray();
                    Assert.Equal(4, buttons.Length);
                    Assert.Single(buttons.Select(b => b.Bounds.Y).Distinct());
                    Assert.Single(buttons.Select(b => b.Bounds.Height).Distinct());
                    Assert.All(buttons, button => Assert.InRange(button.Bounds.Right, 0, row.Bounds.Width));
                }
                var minimum = (double)content.FindResource("Size.ControlMinHeight")!;
                Assert.All(frequencyView.GetVisualDescendants().OfType<NumericUpDown>(), input =>
                {
                    Assert.True(input.Bounds.Height >= minimum);
                    var text = input.GetVisualDescendants().OfType<TextBox>().Single();
                    Assert.True(text.Bounds.Width >= 64);
                });
                Assert.All(inspector.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible), input => Assert.Equal(188, input.Bounds.Width));
                Assert.Equal(before, editor.ToDocument().Nodes.Single().Configuration.GetRawText());
            }
            Capture(window, "shared-collection-editors.png");
        }
        finally
        {
            window.Close();
            L10n.Current.SetLanguage(language);
        }
    }

    [AvaloniaFact]
    public async Task CompactNavigationAndTransferActionsStayWithinTheWindow()
    {
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(folder.Path));
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 600, Height = 900, Content = view };
        var language = L10n.Current.Language;
        window.Show();
        try
        {
            foreach (var locale in new[] { "en", "zh-CN" })
            {
                L10n.Current.SetLanguage(locale);
                await vm.NavigateAsync("waves");
                Dispatcher.UIThread.RunJobs();
                var navigation = view.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == "CompactNav");
                Assert.True(navigation.IsEffectivelyVisible);
                Assert.Equal(6, navigation.Children.Count);
                Assert.All(navigation.Children.OfType<Button>(), button => Assert.InRange(button.Bounds.Right, 0, navigation.Bounds.Width));
                var transfer = view.GetVisualDescendants().OfType<WaveformTransferView>().Single();
                var actions = transfer.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Classes.Contains("actions"));
                Assert.Equal(4, actions.Children.Count);
                Assert.All(actions.Children.OfType<Button>(), button => Assert.InRange(button.Bounds.Right, 0, actions.Bounds.Width));
            }
            await vm.NavigateAsync("settings");
            Dispatcher.UIThread.RunJobs();
            Capture(window, "compact-settings-consistency.png");
        }
        finally
        {
            window.Close();
            L10n.Current.SetLanguage(language);
        }
    }

    private static void Click(Window window, Button button)
    {
        Dispatcher.UIThread.RunJobs();
        var position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(position);
        window.MouseDown(position, MouseButton.Left);
        window.MouseUp(position, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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
