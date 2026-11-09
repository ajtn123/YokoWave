using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify.Avalonia;
using YokoWave.App.ViewModels;
using YokoWave.App.Views;
using YokoWave.App.Views.Components;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class PipelineWorkspaceInteractionTests
{
    [AvaloniaFact]
    public async Task DeviceCardsAssignMultipleOutputsDetachAndLockDuringPlayback()
    {
        using var directory = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(directory.Path));
        await vm.InitializeAsync();
        var editor = vm.Pipelines.Selected!;
        var cards = vm.Pipelines.UnassignedDevices.Where(c => c.Device?.Device.Kind == DeviceKind.DualChannel).ToArray();
        Assert.Equal(2, cards.Length);
        foreach (var card in cards)
        {
            Assert.True(card.AssignmentCommand.CanExecute(null));
            card.AssignmentCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }
        var group = Assert.Single(vm.Pipelines.Groups);
        Assert.Equal(2, group.Devices.Count);
        Assert.Equal(4, editor.Nodes.Count(n => n.Type == BuiltInNodes.ChannelOutput));
        Assert.DoesNotContain(vm.Pipelines.UnassignedDevices, c => cards.Select(d => d.Id).Contains(c.Id));
        foreach (var card in group.Devices.ToArray())
            await vm.Devices.ToggleConnectionAsync(card.Device!);
        await editor.StartAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.All(group.Devices, card => Assert.False(card.AssignmentCommand.CanExecute(null)));
        await editor.StopAsync();
        Dispatcher.UIThread.RunJobs();
        group.Devices.First().AssignmentCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(group.Devices);
        Assert.Equal(2, editor.Nodes.Count(n => n.Type == BuiltInNodes.ChannelOutput));
        Assert.Single(vm.Pipelines.UnassignedDevices, c => cards.Select(d => d.Id).Contains(c.Id));
        await editor.StartAsync();
        await editor.StopAsync();
    }

    [AvaloniaFact]
    public async Task CanvasContextMenuCreatesAtPointerAndWorkspaceUsesCompactUnpaddedLayout()
    {
        using var directory = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(directory.Path));
        await vm.InitializeAsync();
        var editor = vm.Pipelines.Selected!;
        var window = new Window { Width = 1600, Height = 950, Content = new MainView { DataContext = vm }, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var name = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("pipeline-name"));
            Assert.InRange(name.Bounds.Height, 20, 32);
            var template = window.GetVisualDescendants().OfType<ComboBox>().Single(t => t.Classes.Contains("pipeline-create"));
            var create = window.GetVisualDescendants().OfType<Button>().Single(t => t.Classes.Contains("pipeline-create"));
            Assert.Equal(template.Bounds.Height, create.Bounds.Height);
            foreach (var deviceCard in window.GetVisualDescendants().OfType<DeviceCardView>())
            {
                var cardBorder = Assert.IsType<Border>(deviceCard.Content);
                var connect = deviceCard.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("connection-action"));
                var right = connect.TranslatePoint(new Point(connect.Bounds.Width, 0), cardBorder)!.Value.X;
                Assert.True(cardBorder.Bounds.Width - right >= cardBorder.Padding.Right);
                Assert.True(cardBorder.Padding.Right > 0);
            }
            var canvas = window.GetVisualDescendants().OfType<NodifyEditor>().Single();
            var border = Assert.IsType<Border>(canvas.Parent);
            Assert.Equal(default, border.Padding);
            var at = canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height - 80), window)!.Value;
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var menu = canvas.ContextMenu!;
            Assert.True(menu.IsOpen);
            Assert.Equal(window.FontFamily, menu.FontFamily);
            var position = canvas.MouseLocation;
            var add = Assert.IsType<MenuItem>(menu.Items[0]);
            var constant = add.Items.Cast<MenuItem>().Single(i => Equals(i.Header, L10n.Get("Pipeline.Node.Constant")));
            var count = editor.Nodes.Count;
            constant.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(count + 1, editor.Nodes.Count);
            Assert.Equal(position, editor.Nodes.Last().Location);
            menu.Close();
            window.MouseDown(at, MouseButton.Right);
            window.MouseMove(at + new Vector(60, 20));
            window.MouseUp(at + new Vector(60, 20), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.False(menu.IsOpen);
            var card = vm.Pipelines.UnassignedDevices.First(c => c.Device?.Device.Kind == DeviceKind.DualChannel);
            var sharedCard = window.GetVisualDescendants().OfType<DeviceCardView>().Single(v => ReferenceEquals(v.DataContext, card.Device));
            Assert.Same(card.AssignmentCommand, sharedCard.AssignmentCommand);
            Assert.Equal(card.AssignmentAction, sharedCard.AssignmentLabel);
            sharedCard.AssignmentCommand!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            editor.SelectedNodes.Clear();
            editor.SelectedNodes.Add(editor.Nodes.First(n => n.HasStrengthLimit));
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var screenshots = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
            Directory.CreateDirectory(screenshots);
            frame.Save(Path.Combine(screenshots, "pipeline-device-groups.png"), PngBitmapEncoderOptions.Default);
            await vm.Pipelines.SaveAsync();
            var document = await File.ReadAllTextAsync(Path.Combine(directory.Path, "pipelines.json"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("OutputLimit", document);
            Assert.Contains("Limit", document);
        }
        finally { window.Close(); }
    }
}
