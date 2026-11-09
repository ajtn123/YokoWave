using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelineTemplateChoice(PipelineTemplate? builtIn = null, PipelinePreset? preset = null) : ObservableObject
{
    public string Name => builtIn is not null ? L10n.Get(builtIn.NameKey) : preset!.Name;
    public bool IsBuiltIn => builtIn is not null;
    public Guid? PresetId => preset?.Id;
    public PipelineDocument Create(string name, double limit)
    {
        if (builtIn is not null)
            return builtIn.Create(new(name, Limit: limit));
        var graph = preset!.Graph;
        var wrapper = NodeDefinition.Create(SubgraphExpansion.Type, new SubgraphNode(graph, preset.Inputs, preset.Outputs));
        return new(Guid.NewGuid(), name, [wrapper], [], [new(wrapper.Id, 40, 40)], limit, graph.DurationSeconds, graph.TimerEnabled);
    }
    public void RefreshLocalization() => Raise(nameof(Name));
}

public sealed class PipelinesWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly PipelineStore _store;
    private readonly ObservableCollection<PresetItem> _presets;
    private readonly Action<Exception> _onError;
    private readonly Action<SessionRecord> _completed;
    private readonly IAudioInputFactory _audio;
    private readonly Func<double> _timerLimit, _defaultLimit;
    private readonly List<PipelinePreset> _userTemplates = [];
    private PipelineEditorViewModel? _selected;
    private PipelineTemplateChoice? _template;
    private bool _loaded, _disposed;
    private long _revision, _savedRevision;
    private string _templateName = "";
    public ObservableCollection<PipelineEditorViewModel> Pipelines { get; } = [];
    public ObservableCollection<PipelineTemplateChoice> Templates { get; } = [];
    public DevicesPaneViewModel? DevicePane { get; }
    public PipelineTemplateChoice? SelectedTemplate
    {
        get => _template;
        set
        {
            if (Set(ref _template, value))
                DeleteTemplateCommand.NotifyCanExecuteChanged();
        }
    }
    public PipelineEditorViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            DeleteCommand.NotifyCanExecuteChanged();
            SaveTemplateCommand.NotifyCanExecuteChanged();
        }
    }
    public string TemplateName { get => _templateName; set => Set(ref _templateName, value); }
    public bool IsAnyRunning => Pipelines.Any(p => p.Session.HoldsResources);
    public ICommand AddCommand { get; }
    public ICommand DuplicateCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand SaveTemplateCommand { get; }
    public AsyncCommand DeleteTemplateCommand { get; }
    public event EventHandler<WaveformPreset>? WaveformCreated;

    public PipelinesWorkspaceViewModel(DeviceController controller, string directory, ObservableCollection<PresetItem> presets,
        Action<Exception> onError, Action<SessionRecord> completed, Func<double> timerLimit, Func<double> defaultLimit,
        IAudioInputFactory? audio = null, DevicesPaneViewModel? devicePane = null)
    {
        _controller = controller;
        _store = new(directory);
        _presets = presets;
        _onError = onError;
        _completed = completed;
        _timerLimit = timerLimit;
        _defaultLimit = defaultLimit;
        _audio = audio ?? new UnavailableAudioInputFactory();
        DevicePane = devicePane;
        AddCommand = new ActionCommand(Add);
        DuplicateCommand = new ActionCommand(() => { if (Selected is { } p) Selected = AddEditor(Unbind(p.ToDocument()) with { Id = Guid.NewGuid(), Name = L10n.Format("Pipeline.CopyName", p.Name) }); });
        DeleteCommand = new(DeleteAsync, onError, () => Selected?.CanEdit == true);
        SaveCommand = new(SaveAsync, onError);
        SaveTemplateCommand = new(SaveTemplateAsync, onError, () => Selected?.CanEdit == true);
        DeleteTemplateCommand = new(DeleteTemplateAsync, onError, () => SelectedTemplate is { IsBuiltIn: false });
        foreach (var template in PipelineTemplates.All.Concat([RemotePipelineNodes.GattTemplate, RemotePipelineNodes.ControlTemplate]))
            Templates.Add(new(template));
        _template = Templates[0];
        _controller.Changed += OnDevicesChanged;
        L10n.Current.PropertyChanged += OnLanguageChanged;
    }

    public async Task LoadAsync()
    {
        if (_loaded)
            return;
        var documents = await _store.LoadAsync();
        var templates = await _store.LoadPresetsAsync();
        try
        {
            foreach (var preset in templates)
            {
                _userTemplates.Add(preset);
                Templates.Add(new(preset: preset));
            }
            foreach (var document in documents)
                AddEditor(document);
            _loaded = true;
            if (Pipelines.Count == 0)
                Add();
            Selected = Pipelines.FirstOrDefault();
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new LocalizedIOException("Pipeline.Error.Storage", error);
        }
    }
    private PipelineEditorViewModel AddEditor(PipelineDocument document)
    {
        if (Pipelines.Count >= 32)
            throw new LocalizedOperationException("Error.ModeCount");
        var services = new PipelineServices(_audio);
        var registry = RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry());
        var bindings = SubgraphExpansion.Expand(document).Nodes.SelectMany(n => registry.Get(n.Type).Resources(registry.Get(n.Type).Read(n.Configuration)));
        if (bindings.Any(b => !b.DeviceId.StartsWith('$') && Pipelines.Any(p => p.BoundDeviceIds.Contains(b.DeviceId))))
            throw new LocalizedOperationException("Error.DeviceInUse");
        var editor = new PipelineEditorViewModel(document, _controller, _presets, _onError, _completed, _timerLimit(), services, registry);
        editor.CanBindDevice = id => !Pipelines.Any(p => p.Id != editor.Id && p.BoundDeviceIds.Contains(id));
        editor.Session.Changed += OnSessionChanged;
        editor.Edited += OnEdited;
        editor.WaveformCreated += OnWaveformCreated;
        editor.RefreshDevices();
        Pipelines.Add(editor);
        _revision++;
        return editor;
    }
    private void OnWaveformCreated(object? sender, WaveformPreset preset) => WaveformCreated?.Invoke(this, preset);
    public void Add()
    {
        if (!_loaded || _disposed || SelectedTemplate is not { } template)
            return;
        var name = L10n.Format("Mode.InstanceName", template.Name, Pipelines.Count + 1);
        Selected = AddEditor(Unbind(template.Create(name, _defaultLimit())));
    }
    private static PipelineDocument Unbind(PipelineDocument document)
    {
        var registry = RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry());
        var bindings = SubgraphExpansion.Expand(document).Nodes.SelectMany(n => registry.Get(n.Type).Resources(registry.Get(n.Type).Read(n.Configuration)))
            .Select(b => b.DeviceId).Where(id => !id.StartsWith('$')).Distinct().Select((id, index) => (id, replacement: "$device" + (index + 1)))
            .ToDictionary(p => p.id, p => p.replacement);
        return document with { Nodes = document.Nodes.Select(n => SubgraphExpansion.MapDevices(n, bindings)).ToArray() };
    }
    public void AddDocument(PipelineDocument document) => Selected = AddEditor(document);
    private async Task DeleteAsync()
    {
        if (Selected is not { CanEdit: true } editor)
            return;
        await editor.DisposeAsync();
        Unsubscribe(editor);
        Pipelines.Remove(editor);
        _revision++;
        Selected = Pipelines.FirstOrDefault();
    }
    private async Task SaveTemplateAsync()
    {
        if (Selected is not { CanEdit: true } editor)
            return;
        var graph = editor.ToDocument();
        var name = string.IsNullOrWhiteSpace(TemplateName) ? graph.Name : TemplateName;
        if (name.Length > 60)
            throw new LocalizedArgumentException("Pipeline.Error.Document");
        var preset = new PipelinePreset(Guid.NewGuid(), name, PipelineJson.Snapshot(graph), graph.Inputs ?? [], graph.Outputs ?? []);
        await _store.SavePresetsAsync(_userTemplates.Append(preset));
        _userTemplates.Add(preset);
        var choice = new PipelineTemplateChoice(preset: preset);
        Templates.Add(choice);
        SelectedTemplate = choice;
    }
    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is not { IsBuiltIn: false, PresetId: { } id } selected)
            return;
        await _store.SavePresetsAsync(_userTemplates.Where(p => p.Id != id));
        _userTemplates.RemoveAll(p => p.Id == id);
        Templates.Remove(selected);
        SelectedTemplate = Templates.FirstOrDefault();
    }
    private void OnEdited(object? sender, EventArgs e)
    {
        _revision++;
        foreach (var pipeline in Pipelines)
            pipeline.RefreshDevices();
        DeleteCommand.NotifyCanExecuteChanged();
        SaveTemplateCommand.NotifyCanExecuteChanged();
    }
    private void OnSessionChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed)
            return;
        Raise(nameof(IsAnyRunning));
        DeleteCommand.NotifyCanExecuteChanged();
        SaveTemplateCommand.NotifyCanExecuteChanged();
    });
    private void OnDevicesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed)
            return;
        foreach (var pipeline in Pipelines)
            pipeline.RefreshDevices();
    });
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language))
            return;
        foreach (var template in Templates)
            template.RefreshLocalization();
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
            try
            {
                await editor.StopAsync(reason);
            }
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
    public bool UsesWaveform(Guid id)
    {
        if (Pipelines.Any(p => p.UsesWaveform(id)))
            return true;
        var registry = RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry());
        return _userTemplates.Any(p => SubgraphExpansion.Expand(p.Graph).Nodes.Any(n => registry.Get(n.Type).WaveformReferences(registry.Get(n.Type).Read(n.Configuration)).Contains(id)));
    }
    public void RefreshWaveforms()
    {
        foreach (var pipeline in Pipelines)
            pipeline.Validate();
    }
    private void Unsubscribe(PipelineEditorViewModel editor)
    {
        editor.Session.Changed -= OnSessionChanged;
        editor.Edited -= OnEdited;
        editor.WaveformCreated -= OnWaveformCreated;
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
            catch (Exception error) { errors.Add(error); }
            finally { Unsubscribe(editor); }
        }
        try
        {
            await SaveAsync();
        }
        catch (Exception error) { errors.Add(error); }
        _controller.Changed -= OnDevicesChanged;
        L10n.Current.PropertyChanged -= OnLanguageChanged;
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
