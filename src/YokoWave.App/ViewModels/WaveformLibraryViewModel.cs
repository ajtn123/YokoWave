using System.Collections.ObjectModel;
using System.ComponentModel;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed class WaveformLibraryViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<WaveformAsset>, Task> _save;
    private readonly Func<Guid, bool> _inUse;
    private readonly Action _changed;
    private readonly Action<string, object?[]> _notify;
    private readonly Action<Exception> _error;
    private WaveformItem? _selected;
    private string _transferJson = "";
    public ObservableCollection<WaveformItem> Waveforms { get; } = [];
    public WaveformEditorViewModel WaveformEditor { get; } = new();
    public WaveformItem? SelectedWaveform
    {
        get => _selected ?? Waveforms.FirstOrDefault();
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
    public WaveformLibraryViewModel(Func<IReadOnlyList<WaveformAsset>, Task> save, Func<Guid, bool> inUse, Action changed,
        Func<WaveformAsset, Task> use, Func<bool> canUse, Action<string, object?[]> notify, Action<Exception> error)
    {
        _save = save;
        _inUse = inUse;
        _changed = changed;
        _notify = notify;
        _error = error;
        NewWaveCommand = new(WaveformEditor.Clear);
        EditWaveCommand = new(() =>
        {
            if (SelectedWaveform is { } waveform)
                WaveformEditor.Load(waveform);
        });
        SaveWaveCommand = new(SaveWaveAsync, error);
        DeleteWaveCommand = new(DeleteWaveAsync, error, () => SelectedWaveform is { IsBuiltIn: false } selected && !_inUse(selected.Asset.Id));
        UseWaveCommand = new(async () =>
        {
            if (SelectedWaveform is { } waveform)
                await use(waveform.Asset);
        }, error, () => canUse() && SelectedWaveform is not null);
        ExportCommand = new(() => TransferJson = WaveformExchange.Export(Waveforms.Select(p => p.Asset)));
        ImportCommand = new(() => ImportAsync(TransferJson), error);
        L10n.Current.PropertyChanged += OnLanguage;
    }
    public void Load(IEnumerable<WaveformAsset> custom)
    {
        Waveforms.Clear();
        foreach (var waveform in WaveformAsset.BuiltIn.Concat(custom).DistinctBy(p => p.Id))
            Waveforms.Add(new(waveform));
        SelectedWaveform = Waveforms.FirstOrDefault();
    }
    public async Task SaveWaveAsync()
    {
        var waveform = WaveformEditor.BuildWaveform();
        if (_inUse(waveform.Id))
            throw new LocalizedOperationException("Error.WaveInUse");
        await AddAsync(waveform);
        WaveformEditor.MarkSaved(waveform.Id);
    }
    public async Task AddAsync(WaveformAsset waveform)
    {
        waveform.Validate();
        if (DefaultWaveformCatalog.Contains(waveform.Id))
            throw new LocalizedOperationException("Error.DeleteBuiltIn");
        await _save(Waveforms.Where(p => p.Asset.Id != waveform.Id).Select(p => p.Asset).Append(waveform).ToArray());
        if (Waveforms.FirstOrDefault(p => p.Asset.Id == waveform.Id) is { } previous)
            Waveforms.Remove(previous);
        var item = new WaveformItem(waveform);
        Waveforms.Add(item);
        SelectedWaveform = item;
        _changed();
        _notify("Status.WaveSaved", [waveform.GetName(L10n.Current.Culture)]);
    }
    public async Task DeleteWaveAsync()
    {
        if (SelectedWaveform is not { } selected)
            return;
        if (selected.IsBuiltIn)
            throw new LocalizedOperationException("Error.DeleteBuiltIn");
        if (_inUse(selected.Asset.Id))
            throw new LocalizedOperationException("Error.WaveInUse");
        await _save(Waveforms.Where(p => p != selected).Select(p => p.Asset).ToArray());
        Waveforms.Remove(selected);
        SelectedWaveform = Waveforms.FirstOrDefault();
        WaveformEditor.Clear();
        _changed();
        _notify("Status.WaveDeleted", []);
    }
    public async Task ImportAsync(string json)
    {
        var imported = WaveformExchange.Import(json);
        var added = imported.Where(p => Waveforms.All(existing => existing.Asset.Id != p.Id)).ToArray();
        await _save(Waveforms.Select(p => p.Asset).Concat(added).ToArray());
        foreach (var waveform in added)
            Waveforms.Add(new(waveform));
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
    public void Dispose() => L10n.Current.PropertyChanged -= OnLanguage;
    private void OnLanguage(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language))
            return;
        foreach (var waveform in Waveforms)
            waveform.RefreshLocalization();
        foreach (var shape in WaveformEditor.Shapes)
            shape.RefreshLocalization();
        foreach (var row in WaveformEditor.Samples.Rows)
        {
            foreach (var count in row.ChannelCounts)
                count.RefreshLocalization();
        }
    }
}
