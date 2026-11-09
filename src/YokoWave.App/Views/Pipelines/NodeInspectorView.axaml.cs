using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using YokoWave.App.ViewModels.Pipelines;
using YokoWave.Core.Localization;

namespace YokoWave.App.Views.Pipelines;

public sealed partial class NodeInspectorView : UserControl
{
    private NodeEditorViewModel? _boostNode;
    public NodeInspectorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ClearBoost();
        DetachedFromVisualTree += (_, _) => ClearBoost();
    }
    private void ClearBoost()
    {
        if (_boostNode is { } node)
            node.BoostHeld = false;
        _boostNode = null;
    }
    private void BoostPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is NodeEditorViewModel node)
        {
            ClearBoost();
            _boostNode = node;
            node.BoostHeld = true;
        }
        e.Pointer.Capture(sender as Control);
    }
    private void BoostReleased(object? sender, PointerReleasedEventArgs e)
    {
        ClearBoost();
        e.Pointer.Capture(null);
    }
    private void BoostLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ClearBoost();
    }
    private void BoostKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter && DataContext is NodeEditorViewModel node)
        {
            _boostNode = node;
            node.BoostHeld = true;
            e.Handled = true;
        }
    }
    private void BoostKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter && DataContext is NodeEditorViewModel node)
        {
            ClearBoost();
            e.Handled = true;
        }
    }
    private void BoostFocusLost(object? sender, RoutedEventArgs e)
    {
        ClearBoost();
    }
    private async void BrowseMedia(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AudioNodeEditorViewModel node || !node.CanEdit || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new()
            {
                Title = L10n.Get("Audio.File"),
                AllowMultiple = false,
                FileTypeFilter = [new(L10n.Get("Audio.Formats")) { Patterns = ["*.wav", "*.mp3", "*.flac"] }]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && node.CanEdit)
                node.MediaPath = path;
        }
        catch (Exception error) { node.ReportError(error); }
    }
}
