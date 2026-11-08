using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using YokoWave.App.ViewModels;
using YokoWave.Core.Localization;

namespace YokoWave.App.Views.Components;

public sealed partial class AudioModeView : UserControl
{
    public AudioModeView() => InitializeComponent();
    private async void BrowseMediaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PlaybackModeViewModel mode || !mode.CanEditRouting || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new()
            {
                Title = L10n.Get("Audio.File"),
                AllowMultiple = false,
                FileTypeFilter = [new(L10n.Get("Audio.Formats")) { Patterns = ["*.wav", "*.mp3", "*.flac"] }]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && mode.CanEditRouting) mode.MediaPath = path;
        }
        catch (Exception error) { mode.ReportError(error); }
    }
}
