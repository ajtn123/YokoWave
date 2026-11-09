using System.Collections.ObjectModel;
using System.ComponentModel;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class WaveformLibraryViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<WaveformPreset>, Task> _save;
    private readonly Func<Guid, bool> _inUse;
    private readonly Action _changed;
    private readonly Action<string, object?[]> _notify;
    private readonly Action<Exception> _error;
    private PresetItem? _selected;
    private string _transferJson = "";
    public ObservableCollection<PresetItem> Presets { get; } = [];
    public WaveformEditorViewModel WaveformEditor { get; } = new();
    public PresetItem? LibrarySelectedPreset
    {
        get => _selected ?? Presets.FirstOrDefault();
        set
        {
            Set(ref _selected, value);
            DeleteWaveCommand.NotifyCanExecuteChanged();
            UseWaveCommand.NotifyCanExecuteChanged();
        }
    }
    public string TransferJson { get => _transferJson; set => Set(ref _transferJson, value); }
    public ActionCommand NewWaveCommand { get; }
    public ActionCommand EditWaveCommand { get; }
    public AsyncCommand SaveWaveCommand { get; }
    public AsyncCommand DeleteWaveCommand { get; }
    public AsyncCommand UseWaveCommand { get; }
    public ActionCommand ExportCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public WaveformLibraryViewModel(Func<IReadOnlyList<WaveformPreset>, Task> save, Func<Guid, bool> inUse, Action changed,
        Func<WaveformPreset, Task> use, Func<bool> canUse, Action<string, object?[]> notify, Action<Exception> error)
    {
        _save = save;
        _inUse = inUse;
        _changed = changed;
        _notify = notify;
        _error = error;
        NewWaveCommand = new(WaveformEditor.Clear);
        EditWaveCommand = new(() => { if (LibrarySelectedPreset is { } preset) WaveformEditor.Load(preset); });
        SaveWaveCommand = new(SaveWaveAsync, error);
        DeleteWaveCommand = new(DeleteWaveAsync, error, () => LibrarySelectedPreset is { IsBuiltIn: false } selected && !_inUse(selected.Preset.Id));
        UseWaveCommand = new(async () => { if (LibrarySelectedPreset is { } preset) await use(preset.Preset); }, error, () => canUse() && LibrarySelectedPreset is not null);
        ExportCommand = new(() => TransferJson = WorkspaceStore.ExportPresets(Presets.Select(p => p.Preset)));
        ImportCommand = new(() => ImportAsync(TransferJson), error);
        L10n.Current.PropertyChanged += OnLanguage;
    }
    public void Load(IEnumerable<WaveformPreset> custom)
    {
        Presets.Clear();
        foreach (var preset in WaveformPreset.BuiltIn.Concat(custom).DistinctBy(p => p.Id))
            Presets.Add(new(preset));
        LibrarySelectedPreset = Presets.FirstOrDefault();
    }
    public async Task SaveWaveAsync()
    {
        var preset = WaveformEditor.BuildPreset();
        if (_inUse(preset.Id))
            throw new LocalizedOperationException("Error.WaveInUse");
        await AddAsync(preset);
        WaveformEditor.MarkSaved(preset.Id);
    }
    public async Task AddAsync(WaveformPreset preset)
    {
        preset.Validate();
        if (DefaultWaveformCatalog.Contains(preset.Id))
            throw new LocalizedOperationException("Error.DeleteBuiltIn");
        await _save(Presets.Where(p => p.Preset.Id != preset.Id).Select(p => p.Preset).Append(preset).ToArray());
        if (Presets.FirstOrDefault(p => p.Preset.Id == preset.Id) is { } previous)
            Presets.Remove(previous);
        var item = new PresetItem(preset);
        Presets.Add(item);
        LibrarySelectedPreset = item;
        _changed();
        _notify("Status.WaveSaved", [preset.Name]);
    }
    public async Task DeleteWaveAsync()
    {
        if (LibrarySelectedPreset is not { } selected)
            return;
        if (selected.IsBuiltIn)
            throw new LocalizedOperationException("Error.DeleteBuiltIn");
        if (_inUse(selected.Preset.Id))
            throw new LocalizedOperationException("Error.WaveInUse");
        await _save(Presets.Where(p => p != selected).Select(p => p.Preset).ToArray());
        Presets.Remove(selected);
        LibrarySelectedPreset = Presets.FirstOrDefault();
        WaveformEditor.Clear();
        _changed();
        _notify("Status.WaveDeleted", []);
    }
    public async Task ImportAsync(string json)
    {
        var imported = WorkspaceStore.ImportPresets(json);
        var added = imported.Where(p => Presets.All(existing => existing.Preset.Id != p.Id)).ToArray();
        await _save(Presets.Select(p => p.Preset).Concat(added).ToArray());
        foreach (var preset in added)
            Presets.Add(new(preset));
        _changed();
        _notify("Status.Imported", [added.Length, imported.Count - added.Length]);
    }
    public void RefreshCommands()
    {
        UseWaveCommand.NotifyCanExecuteChanged();
        DeleteWaveCommand.NotifyCanExecuteChanged();
    }
    public void ShowError(Exception error) => _error(error);
    public void Notify(string key, params object?[] arguments) => _notify(key, arguments);
    private void OnLanguage(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language))
            return;
        foreach (var preset in Presets)
            preset.RefreshLocalization();
        foreach (var shape in WaveformEditor.Shapes)
            shape.RefreshLocalization();
        foreach (var row in WaveformEditor.Samples.Rows)
            foreach (var count in row.ChannelCounts)
                count.RefreshLocalization();
    }
    public void Dispose() => L10n.Current.PropertyChanged -= OnLanguage;
}
