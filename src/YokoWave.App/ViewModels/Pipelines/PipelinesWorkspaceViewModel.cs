using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelinesWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly PipelineStore _store;
    private readonly ObservableCollection<PresetItem> _presets;
    private readonly Action<Exception> _onError;
    private readonly Action<SessionRecord> _completed;
    private readonly Func<double> _timerLimit;
    private readonly Func<double> _defaultLimit;
    private PipelineEditorViewModel? _selected;
    private bool _loaded, _disposed;
    private long _revision, _savedRevision;
    public ObservableCollection<PipelineEditorViewModel> Pipelines { get; } = [];
    public PipelineEditorViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                DeleteCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsAnyRunning => Pipelines.Any(p => !p.CanEdit);
    public ICommand AddCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand SaveCommand { get; }

    public PipelinesWorkspaceViewModel(DeviceController controller, string directory, ObservableCollection<PresetItem> presets,
        Action<Exception> onError, Action<SessionRecord> completed, Func<double> timerLimit, Func<double> defaultLimit)
    {
        _controller = controller;
        _store = new(directory);
        _presets = presets;
        _onError = onError;
        _completed = completed;
        _timerLimit = timerLimit;
        _defaultLimit = defaultLimit;
        AddCommand = new ActionCommand(Add);
        DeleteCommand = new(DeleteAsync, onError, () => Selected?.CanEdit == true);
        SaveCommand = new(SaveAsync, onError);
        _controller.Changed += OnDevicesChanged;
        L10n.Current.PropertyChanged += OnLanguageChanged;
    }

    public async Task LoadAsync()
    {
        if (_loaded)
            return;
        var documents = await _store.LoadAsync();
        try
        {
            foreach (var document in documents)
                AddEditor(document);
            _loaded = true;
            if (Pipelines.Count == 0)
                Add();
            Selected = Pipelines.First();
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new LocalizedIOException("Pipeline.Error.Storage", error);
        }
    }

    private PipelineEditorViewModel AddEditor(PipelineDocument document)
    {
        var editor = new PipelineEditorViewModel(document, _controller, _presets, _onError, _completed, _timerLimit());
        editor.Session.Changed += OnSessionChanged;
        editor.Edited += OnEdited;
        editor.RefreshDevices();
        Pipelines.Add(editor);
        return editor;
    }

    public void Add()
    {
        if (!_loaded || _disposed)
            return;
        var waveform = NodeDefinition.Create(BuiltInNodes.Waveform, new WaveformNode(_presets.First().Preset.Id));
        var strength = NodeDefinition.Create(BuiltInNodes.Constant, new ConstantNode());
        var gain = NodeDefinition.Create(BuiltInNodes.Gain, new GainNode());
        var output = NodeDefinition.Create(BuiltInNodes.ChannelOutput, new ChannelOutputNode());
        Selected = AddEditor(new(Guid.NewGuid(), L10n.Format("Pipeline.DefaultName", Pipelines.Count + 1), [waveform, strength, gain, output],
            [new(new(waveform.Id, "level"), new(gain.Id, "value")), new(new(strength.Id, "value"), new(gain.Id, "gain")),
             new(new(gain.Id, "value"), new(output.Id, "strength")), new(new(waveform.Id, "frequency"), new(output.Id, "frequency")),
             new(new(waveform.Id, "pulseWidth"), new(output.Id, "pulseWidth"))],
            [new(waveform.Id, 40, 40), new(strength.Id, 40, 260), new(gain.Id, 320, 120), new(output.Id, 600, 80)], _defaultLimit()));
        _revision++;
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { CanEdit: true } editor)
            return;
        await editor.DisposeAsync();
        editor.Session.Changed -= OnSessionChanged;
        editor.Edited -= OnEdited;
        Pipelines.Remove(editor);
        _revision++;
        Selected = Pipelines.FirstOrDefault();
    }

    private void OnEdited(object? sender, EventArgs e)
    {
        _revision++;
        DeleteCommand.NotifyCanExecuteChanged();
    }
    private void OnSessionChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        Raise(nameof(IsAnyRunning));
        DeleteCommand.NotifyCanExecuteChanged();
    });
    private void OnDevicesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
            foreach (var pipeline in Pipelines)
                pipeline.RefreshDevices();
    });
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JsonLocalizer.Language))
            foreach (var pipeline in Pipelines)
                pipeline.RefreshLocalization();
    }

    public async Task SaveAsync()
    {
        if (!_loaded || _savedRevision == _revision && File.Exists(_store.FilePath))
            return;
        var revision = _revision;
        await _store.SaveAsync(Pipelines.Select(p => p.ToDocument()));
        _savedRevision = Math.Max(_savedRevision, revision);
    }
    public void RefreshTimerLimit()
    {
        foreach (var pipeline in Pipelines)
            pipeline.SetTimerLimit(_timerLimit());
        _revision++;
    }
    public async Task StopAllAsync(string reason)
    {
        List<Exception> errors = [];
        foreach (var editor in Pipelines)
        {
            try { await editor.StopAsync(reason); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
    }
    public async Task StopForDeviceAsync(string id, string reason)
    {
        foreach (var editor in Pipelines.Where(p => p.BoundDeviceIds.Contains(id)))
            await editor.StopAsync(reason);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        List<Exception> errors = [];
        foreach (var editor in Pipelines)
        {
            try
            {
                await editor.DisposeAsync();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                editor.Session.Changed -= OnSessionChanged;
                editor.Edited -= OnEdited;
            }
        }
        try { await SaveAsync(); }
        catch (Exception error) { errors.Add(error); }
        _controller.Changed -= OnDevicesChanged;
        L10n.Current.PropertyChanged -= OnLanguageChanged;
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
