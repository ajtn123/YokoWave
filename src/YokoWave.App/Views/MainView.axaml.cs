using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using YokoWave.App.ViewModels;
using YokoWave.Core;

namespace YokoWave.App.Views;

public sealed partial class MainView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _initialized;
    public MainView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => { if (DataContext is MainViewModel vm) await vm.TickAsync(); };
        Loaded += async (_, _) =>
        {
            if (!_initialized && DataContext is MainViewModel vm)
            {
                _initialized = true;
                try { await vm.InitializeAsync(); }
                catch (Exception e) { vm.ShowError(e); }
            }
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var compact = Bounds.Width < 1000;
        Sidebar.IsVisible = !compact;
        Shell.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 194);
        CompactNav.IsVisible = compact;
        ModeBadge.IsVisible = Bounds.Width > 800;
        var stacked = Bounds.Width < 850;
        WorkspaceGrid.ColumnDefinitions = ColumnDefinitions.Parse(stacked ? "*" : "264,22,*");
        WorkspaceGrid.RowDefinitions = RowDefinitions.Parse(stacked ? "Auto,24,Auto" : "Auto");
        Grid.SetColumn(ControlPane, stacked ? 0 : 2);
        Grid.SetRow(ControlPane, stacked ? 2 : 0);
    }

    private async void ImportFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new()
            {
                Title = "导入 YokoWave 波形", AllowMultiple = false,
                FileTypeFilter = [new("波形 JSON") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            // Bound memory even when a file picker supplies an unusually large stream.
            var buffer = new char[1_000_001];
            var length = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (length > 1_000_000) throw new ArgumentException("波形文件不能超过 1 MB。");
            vm.TransferJson = new string(buffer, 0, length);
            await vm.ImportAsync(vm.TransferJson);
        }
        catch (Exception error) { vm.ShowError(error); }
    }

    private async void ExportFileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var file = await storage.SaveFilePickerAsync(new()
            {
                Title = "导出 YokoWave 波形", SuggestedFileName = "yokowave-presets.json",
                DefaultExtension = "json", FileTypeChoices = [new("波形 JSON") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(WorkspaceStore.ExportPresets(vm.Presets.Select(p => p.Preset)));
            vm.Notify("波形文件已导出。");
        }
        catch (Exception error) { vm.ShowError(error); }
    }
}
