using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using YokoWave.App.ViewModels;
using YokoWave.App.Views;
using YokoWave.App.Views.Components;
using YokoWave.App.Views.Pages;
using YokoWave.Core;
using YokoWave.Tests.Support.Ui;

namespace YokoWave.Tests.Ui;

public sealed class WaveformLibraryTests
{
    [AvaloniaFact]
    public async Task SplitViewsProtectDefaultsAndPreserveSamplesWhenDuplicating()
    {
        using var state = new UiTestState();
        using var folder = new TestFolder();
        await using var vm = new MainViewModel(new DeviceController(new SimulationTransport()), new WorkspaceStore(folder.Path));
        var view = new MainView
        {
            DataContext = vm
        };
        var window = new Window
        {
            Content = view,
            Width = 1000,
            Height = 600
        };
        try
        {
            window.Show();
            await UiActions.EventuallyAsync(() => vm.Waveforms.Waveforms.Count == DefaultWaveformCatalog.Waveforms.Count && !vm.Devices.IsScanning);
            Assert.NotNull(Find<DeviceWorkspaceView>(view, "WorkspacePage"));
            Assert.NotNull(Find<WaveformLibraryView>(view, "WavesPage"));
            Assert.NotNull(Find<SessionHistoryView>(view, "HistoryPage"));
            Assert.NotNull(Find<SettingsView>(view, "SettingsPage"));
            Assert.Single(view.GetLogicalDescendants().OfType<WaveformEditorView>());
            Assert.Single(view.GetLogicalDescendants().OfType<WaveformTransferView>());
            await vm.NavigateAsync("waves");
            vm.Waveforms.SelectedWaveform = vm.Waveforms.Waveforms.MaxBy(wave => wave.Asset.Samples!.Length);
            var original = vm.Waveforms.SelectedWaveform!.Asset;
            Assert.True(original.Samples!.Length > 256);
            Assert.False(vm.Waveforms.DeleteWaveCommand.CanExecute(null));
            Assert.False(Find<Button>(view, "DeleteWaveButton").IsEffectivelyEnabled);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(vm.Waveforms.DeleteWaveAsync);
            vm.Waveforms.EditWaveCommand.Execute(null);
            await vm.Waveforms.SaveWaveAsync();
            Assert.Equal(DefaultWaveformCatalog.Waveforms.Count + 1, vm.Waveforms.Waveforms.Count);
            Assert.False(vm.Waveforms.SelectedWaveform!.IsBuiltIn);
            Assert.Equivalent(original.Samples, vm.Waveforms.SelectedWaveform.Asset.Samples);
            Assert.True(vm.Waveforms.DeleteWaveCommand.CanExecute(null));
            Assert.True(Find<Button>(view, "DeleteWaveButton").IsEffectivelyEnabled);
            UiActions.Click(window, Find<Button>(view, "DeleteWaveButton"));
            await UiActions.EventuallyAsync(() => vm.Waveforms.Waveforms.Count == DefaultWaveformCatalog.Waveforms.Count);
            Assert.False(vm.Waveforms.DeleteWaveCommand.CanExecute(null));
            UiArtifacts.Capture(window, "default-waveforms.png");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void SampleEditorPreservesLongBuiltInsAndStopsAddingAtTheSharedLimit()
    {
        var waveform = DefaultWaveformCatalog.Waveforms.MaxBy(wave => wave.Samples!.Length)!;
        var editor = new WaveformEditorViewModel();
        editor.Load(new(waveform));
        Assert.Equivalent(waveform.Samples, editor.Samples.Snapshot());
        while (editor.Samples.Rows.Count < WaveformAsset.MaximumSamples)
        {
            Assert.True(editor.Samples.AddCommand.CanExecute(null));
            editor.Samples.AddCommand.Execute(null);
        }
        Assert.False(editor.Samples.AddCommand.CanExecute(null));
        editor.Samples.AddCommand.Execute(null);
        Assert.Equal(WaveformAsset.MaximumSamples, editor.Samples.Rows.Count);
    }

    private static T Find<T>(Control root, string name)
        where T : Control
    {
        Dispatcher.UIThread.RunJobs();
        return root.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    }
}
