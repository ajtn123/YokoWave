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
using YokoWave.App.Views.Pipelines;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using Xunit;

namespace YokoWave.Tests;

public sealed class PipelineWorkspaceInteractionTests
{
    [AvaloniaFact]
    public async Task ManagementMenuTargetsRightClickedPipelineAndSavesNamedTemplates()
    {
        using var directory = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(directory.Path));
        await vm.InitializeAsync();
        var first = vm.Pipelines.Selected!;
        vm.Pipelines.Add();
        Assert.NotSame(first, vm.Pipelines.Selected);
        var window = new Window { Width = 1600, Height = 950, Content = new MainView { DataContext = vm } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var group = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("mode-group") && b.DataContext is PipelineDeviceGroupViewModel g && g.Editor == first);
            var at = group.TranslatePoint(new Point(group.Bounds.Width / 2, 25), window)!.Value;
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(first, vm.Pipelines.Selected);
            var list = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("pipeline-list"));
            var menu = list.ContextMenu!;
            Assert.True(menu.IsOpen);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Same(vm.Pipelines.DuplicateCommand, items.Single(i => Equals(i.Header, L10n.Get("Pipeline.Duplicate"))).Command);
            Assert.Same(vm.Pipelines.SaveCommand, items.Single(i => Equals(i.Header, L10n.Get("Pipeline.Save"))).Command);
            Assert.Same(vm.Pipelines.DeleteCommand, items.Single(i => Equals(i.Header, L10n.Get("Pipeline.Delete"))).Command);
            Assert.Same(vm.Pipelines.DeleteTemplateCommand, items.Single(i => Equals(i.Header, L10n.Get("Pipeline.DeleteTemplate"))).Command);
            var save = items.Single(i => Equals(i.Header, L10n.Get("Pipeline.SaveTemplate")));
            var popup = TopLevel.GetTopLevel(save)!;
            var saveAt = save.TranslatePoint(new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), popup)!.Value;
            popup.MouseMove(saveAt);
            popup.MouseDown(saveAt, MouseButton.Left);
            popup.MouseUp(saveAt, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.IsType<SavePipelineTemplateWindow>(Assert.Single(window.OwnedWindows));
            var input = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(input.IsFocused);
            dialog.KeyTextInput("custom pipeline template");
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            for (var i = 0; i < 100 && vm.Pipelines.SelectedTemplate?.Name != "custom pipeline template"; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Assert.Equal("custom pipeline template", vm.Pipelines.SelectedTemplate?.Name);
            Assert.Empty(window.OwnedWindows);
            Assert.True(vm.Pipelines.DeleteTemplateCommand.CanExecute(null));
            Assert.Same(first, vm.Pipelines.Selected);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray())
                dialog.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DeviceMembershipDoesNotAddNodesPersistsAndLocksDuringPlayback()
    {
        using var directory = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(directory.Path));
        await vm.InitializeAsync();
        var editor = vm.Pipelines.Selected!;
        Assert.Empty(vm.Pipelines.UnassignedDevices);
        foreach (var device in vm.Devices.Devices.Where(d => d.Device.Kind == DeviceKind.DualChannel))
            await vm.Devices.ToggleConnectionAsync(device);
        Dispatcher.UIThread.RunJobs();
        var cards = vm.Pipelines.UnassignedDevices.Where(c => c.Device?.Device.Kind == DeviceKind.DualChannel).ToArray();
        Assert.Equal(2, cards.Length);
        var nodeIds = editor.Nodes.Select(n => n.Id).ToArray();
        var connections = editor.Connections.Select(c => c.Definition).ToArray();
        var layout = editor.ToDocument().Layout;
        foreach (var card in cards)
        {
            Assert.True(card.AssignmentCommand.CanExecute(null));
            card.AssignmentCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }
        var group = Assert.Single(vm.Pipelines.Groups);
        Assert.Equal(2, group.Devices.Count);
        Assert.Equal(nodeIds, editor.Nodes.Select(n => n.Id));
        Assert.Equal(connections, editor.Connections.Select(c => c.Definition));
        Assert.Equal(layout, editor.ToDocument().Layout);
        Assert.Equal(2, editor.Nodes.Count(n => n.Type == BuiltInNodes.ChannelOutput));
        Assert.All(editor.Nodes.OfType<ChannelOutputEditorViewModel>(), n => Assert.Equal(cards[0].Id, n.DeviceId));
        Assert.Contains(cards[1].Id, editor.AssignedDeviceIds);
        Assert.DoesNotContain(cards[1].Id, editor.ReferencedDeviceIds);
        await vm.Pipelines.SaveAsync();
        var stored = Assert.Single(await new PipelineStore(directory.Path).LoadAsync());
        Assert.Equal(2, stored.AssignedDevices!.Length);
        Assert.DoesNotContain(vm.Pipelines.UnassignedDevices, c => cards.Select(d => d.Id).Contains(c.Id));
        await editor.StartAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.All(group.Devices, card => Assert.False(card.AssignmentCommand.CanExecute(null)));
        await editor.StopAsync();
        Dispatcher.UIThread.RunJobs();
        group.Devices.Last().AssignmentCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(group.Devices);
        Assert.Equal(nodeIds, editor.Nodes.Select(n => n.Id));
        Assert.Equal(connections, editor.Connections.Select(c => c.Definition));
        Assert.Equal(2, editor.Nodes.Count(n => n.Type == BuiltInNodes.ChannelOutput));
        Assert.Single(vm.Pipelines.UnassignedDevices, c => cards.Select(d => d.Id).Contains(c.Id));
        await editor.StartAsync();
        await editor.StopAsync();
        var remaining = Assert.Single(group.Devices).Device;
        await vm.Devices.ToggleConnectionAsync(remaining);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(group.Devices);
        Assert.Contains(remaining.Id, editor.AssignedDeviceIds);
        Assert.DoesNotContain(editor.Devices, d => d.Id == remaining.Id);
        await vm.Devices.ToggleConnectionAsync(remaining);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(remaining, Assert.Single(group.Devices).Device);
        Assert.Single(group.Devices).AssignmentCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(group.Devices);
        Assert.Empty(editor.AssignedDeviceIds);
        Assert.All(editor.ReferencedDeviceIds, id => Assert.StartsWith("$", id));
        Assert.Equal(nodeIds, editor.Nodes.Select(n => n.Id));
        Assert.Equal(connections, editor.Connections.Select(c => c.Definition));
        Assert.Equal(layout, editor.ToDocument().Layout);
    }

    [AvaloniaFact]
    public async Task DevicesCanJoinWithoutOutputSlotsAndOnlyReferencedDevicesAreUsedAtRuntime()
    {
        using var directory = new TestFolder();
        var controller = new DeviceController(new SimulationTransport());
        await using var vm = new MainViewModel(controller, new(directory.Path));
        await vm.InitializeAsync();
        foreach (var device in vm.Devices.Devices.Where(d => d.Device.Kind == DeviceKind.DualChannel))
            await vm.Devices.ToggleConnectionAsync(device);
        Dispatcher.UIThread.RunJobs();
        var editor = vm.Pipelines.Selected!;
        foreach (var node in editor.Nodes)
            editor.SelectedNodes.Add(node);
        editor.DeleteSelectionCommand.Execute(null);
        Assert.Empty(editor.Nodes);
        var first = vm.Pipelines.UnassignedDevices[0];
        first.AssignmentCommand.Execute(null);
        Assert.Empty(editor.Nodes);
        Assert.Contains(first.Id, editor.AssignedDeviceIds);
        Assert.False(editor.CanStart);
        var output = Assert.IsType<ChannelOutputEditorViewModel>(editor.AddNode(BuiltInNodes.ChannelOutput));
        var second = vm.Pipelines.UnassignedDevices.Single();
        second.AssignmentCommand.Execute(null);
        Assert.Equal(output.Id, Assert.Single(editor.Nodes).Id);
        Assert.Equal(second.Id, Assert.Single(editor.Nodes.OfType<ChannelOutputEditorViewModel>()).DeviceId);
        Assert.Empty(editor.Connections);
        vm.Pipelines.Add();
        var other = vm.Pipelines.Selected!;
        Assert.ThrowsAny<InvalidOperationException>(() => other.RebindDevice("$device", first.Id));
        await editor.StartAsync();
        Assert.Null(controller.DeviceOwner(first.Id));
        Assert.Equal(editor.Session.OwnerId, controller.DeviceOwner(second.Id));
        await vm.Devices.ToggleConnectionAsync(first.Device);
        Assert.Equal(PipelineState.Running, editor.Session.State);
        await editor.StopAsync();
        await vm.Pipelines.SaveAsync();
        var stored = (await new PipelineStore(directory.Path).LoadAsync()).Single(p => p.Id == editor.Id);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), stored.AssignedDevices!.Order());
    }

    [AvaloniaFact]
    public async Task AutomaticSlotAssignmentDoesNotBindTwoOutputsToTheSameChannel()
    {
        using var directory = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new(directory.Path));
        await vm.InitializeAsync();
        foreach (var device in vm.Devices.Devices.Where(d => d.Device.Kind == DeviceKind.DualChannel))
            await vm.Devices.ToggleConnectionAsync(device);
        Dispatcher.UIThread.RunJobs();
        var editor = vm.Pipelines.Selected!;
        foreach (var node in editor.Nodes)
            editor.SelectedNodes.Add(node);
        editor.DeleteSelectionCommand.Execute(null);
        editor.AddNode(BuiltInNodes.ChannelOutput);
        editor.AddNode(BuiltInNodes.ChannelOutput);
        var nodeIds = editor.Nodes.Select(n => n.Id).ToArray();
        foreach (var card in vm.Pipelines.UnassignedDevices.ToArray())
            card.AssignmentCommand.Execute(null);
        Assert.Equal(nodeIds, editor.Nodes.Select(n => n.Id));
        Assert.Equal(2, editor.Nodes.OfType<ChannelOutputEditorViewModel>().Select(n => n.DeviceId).Distinct().Count());
        Assert.True(editor.CanStart);
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
        foreach (var device in vm.Devices.Devices.Where(d => d.Device.Kind == DeviceKind.DualChannel))
            await vm.Devices.ToggleConnectionAsync(device);
        var window = new Window { Width = 1600, Height = 950, Content = new MainView { DataContext = vm }, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var name = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("pipeline-name"));
            var template = window.GetVisualDescendants().OfType<ComboBox>().Single(t => t.Classes.Contains("pipeline-create"));
            var create = window.GetVisualDescendants().OfType<Button>().Single(t => t.Classes.Contains("pipeline-create"));
            Assert.Equal(template.Bounds.Height, create.Bounds.Height);
            Assert.Equal(template.Bounds.Height, name.Bounds.Height);
            var label = create.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == L10n.Get("Mode.Add"));
            var center = label.TranslatePoint(new Point(0, label.Bounds.Height / 2), create)!.Value.Y;
            Assert.InRange(Math.Abs(center - create.Bounds.Height / 2), 0, 1);
            var workspace = window.GetVisualDescendants().OfType<PipelinesWorkspaceView>().Single();
            var pipelineCards = workspace.GetVisualDescendants().OfType<DeviceCardView>().ToArray();
            Assert.Equal(2, pipelineCards.Length);
            foreach (var deviceCard in pipelineCards)
            {
                Assert.True(Assert.IsType<DeviceItem>(deviceCard.DataContext).IsConnected);
                Assert.False(deviceCard.ShowConnectionAction);
                var connect = deviceCard.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("connection-action"));
                Assert.False(connect.IsVisible);
            }
            Assert.DoesNotContain(workspace.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.Command, vm.Devices.ScanCommand));
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
            Assert.False(editor.DeleteSelectionCommand.CanExecute(null));
            var delete = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, L10n.Get("Pipeline.Delete")));
            Assert.False(delete.IsEffectivelyEnabled);
            Assert.Contains(menu.Items.OfType<MenuItem>(), i => Equals(i.Header, L10n.Get("Pipeline.Fit")));
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
            var sharedCard = workspace.GetVisualDescendants().OfType<DeviceCardView>().Single(v => ReferenceEquals(v.DataContext, card.Device));
            Assert.Same(card.AssignmentCommand, sharedCard.AssignmentCommand);
            Assert.Equal(card.AssignmentAction, sharedCard.AssignmentLabel);
            sharedCard.AssignmentCommand!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            editor.SelectedNodes.Clear();
            editor.SelectedNodes.Add(editor.Nodes.First(n => n.HasStrengthLimit));
            Dispatcher.UIThread.RunJobs();
            var toolbar = workspace.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == "PlaybackToolbar");
            var playback = toolbar.Children.OfType<Button>().Single(b => b.Name == "PlaybackButton");
            var timer = toolbar.GetVisualDescendants().OfType<PipelineTimerView>().Single();
            var slider = timer.FindControl<Slider>("PipelineDurationSlider")!;
            var progress = timer.FindControl<ProgressBar>("PipelineTimerProgress")!;
            Assert.All(toolbar.Children, control => Assert.Equal(template.Bounds.Height, control.Bounds.Height));
            Assert.DoesNotContain(toolbar.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, L10n.Get("Pipeline.Fit")) || Equals(b.Content, L10n.Get("Pipeline.Pause")));
            Assert.False(slider.IsVisible);
            Assert.False(progress.IsVisible);
            Assert.InRange(timer.Bounds.Width, 1, 120);
            var playbackBounds = playback.Bounds;
            Assert.True(double.IsNaN(playback.Width));
            Assert.Equal(L10n.Get("Pipeline.Start"), playback.Content);
            Assert.True(editor.DeleteSelectionCommand.CanExecute(null));
            editor.TimerEnabled = true;
            editor.DurationSeconds = 90;
            Dispatcher.UIThread.RunJobs();
            Assert.True(slider.IsVisible);
            Assert.InRange(timer.Bounds.Width, 1, 245);
            foreach (var checkbox in toolbar.GetVisualDescendants().OfType<CheckBox>())
            {
                var checkboxLabel = checkbox.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible);
                var labelCenter = checkboxLabel.TranslatePoint(new Point(0, checkboxLabel.Bounds.Height / 2), checkbox)!.Value.Y;
                Assert.InRange(Math.Abs(labelCenter - checkbox.Bounds.Height / 2), 0, 1);
                var square = checkbox.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NormalRectangle");
                var squareCenter = square.TranslatePoint(new Point(0, square.Bounds.Height / 2), checkbox)!.Value.Y;
                Assert.InRange(Math.Abs(squareCenter - labelCenter), 0, 1);
            }
            var thumb = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            var thumbCenter = thumb.TranslatePoint(new Point(0, thumb.Bounds.Height / 2), toolbar)!.Value.Y;
            Assert.InRange(Math.Abs(thumbCenter - toolbar.Bounds.Height / 2), 0, 1);
            using (var timerFrame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(timerFrame);
                var timerScreenshots = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
                Directory.CreateDirectory(timerScreenshots);
                timerFrame.Save(Path.Combine(timerScreenshots, "pipeline-toolbar-timer.png"), PngBitmapEncoderOptions.Default);
            }
            await editor.TogglePlaybackCommand.ExecuteAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PipelineState.Running, editor.Session.State);
            Assert.Equal(L10n.Get("Pipeline.Stop"), playback.Content);
            Assert.Equal(playbackBounds.Position, playback.Bounds.Position);
            Assert.Equal(playbackBounds.Height, playback.Bounds.Height);
            Assert.False(slider.IsVisible);
            Assert.True(progress.IsEffectivelyVisible);
            Assert.DoesNotContain(timer.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible);
            var track = Assert.IsType<Grid>(progress.Parent);
            Assert.InRange(progress.Bounds.Left, 0, track.Bounds.Width);
            Assert.InRange(progress.Bounds.Right, 0, track.Bounds.Width);
            var timerReadout = timer.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("pipeline-timer-readout"));
            var progressRight = progress.TranslatePoint(new Point(progress.Bounds.Width, 0), timer)!.Value.X;
            var readoutLeft = timerReadout.TranslatePoint(default, timer)!.Value.X;
            Assert.True(progressRight <= readoutLeft);
            using (var runningFrame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(runningFrame);
                var runningScreenshots = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots"));
                Directory.CreateDirectory(runningScreenshots);
                runningFrame.Save(Path.Combine(runningScreenshots, "pipeline-toolbar-running.png"), PngBitmapEncoderOptions.Default);
            }
            Assert.False(editor.DeleteSelectionCommand.CanExecute(null));
            Assert.DoesNotContain(workspace.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible &&
                (t.Text == L10n.Get("Pipeline.State.Idle") || t.Text == L10n.Get("Pipeline.State.Running")));
            await editor.TogglePlaybackCommand.ExecuteAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PipelineState.Idle, editor.Session.State);
            Assert.Equal(L10n.Get("Pipeline.Start"), playback.Content);
            Assert.Equal(playbackBounds, playback.Bounds);
            Assert.True(slider.IsVisible);
            editor.TimerEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(slider.IsVisible);
            Assert.Equal(90, editor.DurationSeconds);
            var fit = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, L10n.Get("Pipeline.Fit")));
            fit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
            await vm.NavigateAsync("devices");
            Dispatcher.UIThread.RunJobs();
            var deviceCards = window.GetVisualDescendants().OfType<DeviceCardView>().Where(v => v.IsEffectivelyVisible).ToArray();
            Assert.Equal(vm.Devices.Devices.Count, deviceCards.Length);
            foreach (var deviceCard in deviceCards)
            {
                Assert.True(deviceCard.ShowConnectionAction);
                var cardBorder = Assert.IsType<Border>(deviceCard.Content);
                var connect = deviceCard.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("connection-action"));
                Assert.True(connect.IsEffectivelyVisible);
                var right = connect.TranslatePoint(new Point(connect.Bounds.Width, 0), cardBorder)!.Value.X;
                Assert.True(cardBorder.Bounds.Width - right >= cardBorder.Padding.Right);
                Assert.True(cardBorder.Padding.Right > 0);
            }
            using var devicesFrame = window.CaptureRenderedFrame();
            Assert.NotNull(devicesFrame);
            devicesFrame.Save(Path.Combine(screenshots, "device-auto-connect.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }
}
