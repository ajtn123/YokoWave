using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;
using YokoWave.Remote;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelinesWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly PipelineStore _store;
    private readonly ObservableCollection<WaveformItem> _waveforms;
    private readonly Action<SessionRecord> _completed;
    private readonly IAudioInputFactory _audio;
    private readonly Func<double> _timerLimit;
    private readonly Func<double> _defaultLimit;
    private readonly List<SavedPipelineTemplate> _userTemplates = [];
    private PipelineEditorViewModel? _selected;
    private PipelineTemplateChoice? _template;
    private bool _loaded;
    private bool _disposed;
    private long _revision;
    private long _savedRevision;
    private string _templateName = "";
    public Action<Exception> ReportError { get; }
    public ObservableCollection<PipelineEditorViewModel> Pipelines { get; } = [];
    public PipelineDeviceGroupsViewModel DeviceGroups { get; }
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
            RefreshDeviceGroups();
            Raise(nameof(CanEditSelected));
            DeleteCommand.NotifyCanExecuteChanged();
            SaveTemplateCommand.NotifyCanExecuteChanged();
        }
    }
    public string TemplateName { get => _templateName; set => Set(ref _templateName, value); }
    public bool CanEditSelected => Selected?.CanEdit == true;
    public bool IsAnyRunning => Pipelines.Any(p => p.Session.HoldsResources);
    public ICommand AddCommand { get; }
    public ICommand DuplicateCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand SaveTemplateCommand { get; }
    public AsyncCommand DeleteTemplateCommand { get; }
    public event EventHandler<WaveformAsset>? WaveformCreated;

    public PipelinesWorkspaceViewModel(DeviceController controller, string directory, ObservableCollection<WaveformItem> waveforms,
        Action<Exception> onError, Action<SessionRecord> completed, Func<double> timerLimit, Func<double> defaultLimit,
        IAudioInputFactory? audio = null, DevicesPaneViewModel? devicePane = null)
    {
        _controller = controller;
        _store = new(directory);
        _waveforms = waveforms;
        ReportError = onError;
        _completed = completed;
        _timerLimit = timerLimit;
        _defaultLimit = defaultLimit;
        _audio = audio ?? new UnavailableAudioInputFactory();
        DevicePane = devicePane;
        DeviceGroups = new(() => Selected, editor => Selected = editor, onError);
        AddCommand = new ActionCommand(Add);
        DuplicateCommand = new ActionCommand(Duplicate);
        DeleteCommand = new(DeleteAsync, onError, () => CanEditSelected);
        SaveCommand = new(SaveAsync, onError);
        SaveTemplateCommand = new(SaveTemplateAsync, onError, () => CanEditSelected);
        DeleteTemplateCommand = new(DeleteTemplateAsync, onError, () => SelectedTemplate is { IsBuiltIn: false });
        foreach (var template in PipelineTemplates.All.Concat([RemotePipelineNodes.GattTemplate, RemotePipelineNodes.ControlTemplate, RemoteEventNodes.Template,
            Remote.DgLab.V4.DgLabV4Nodes.AppTemplate, Remote.DgLab.V4.DgLabV4Nodes.SensorTemplate, Remote.DgLab.V4.DgLabV4Nodes.RelayTemplate]))
        {
            Templates.Add(new(template));
        }

        _template = Templates[0];
        Pipelines.CollectionChanged += OnDeviceCollectionChanged;
        if (DevicePane is not null)
            DevicePane.Devices.CollectionChanged += OnDeviceCollectionChanged;
        _controller.Changed += OnDevicesChanged;
        L10n.Current.PropertyChanged += OnLanguageChanged;
    }

    public async Task LoadAsync()
    {
        if (_loaded)
            return;
        var documents = await _store.LoadAsync();
        var templates = await _store.LoadTemplatesAsync();
        try
        {
            foreach (var templateAsset in templates)
            {
                _userTemplates.Add(templateAsset);
                Templates.Add(new(savedTemplate: templateAsset));
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
    public void Add()
    {
        if (!_loaded || _disposed || SelectedTemplate is not { } template)
            return;
        string name = FormatGeneratedName(template.Name, stem => L10n.Format("Mode.InstanceName", stem, Pipelines.Count + 1));
        Selected = AddEditor(Unbind(template.Create(name, _defaultLimit())));
    }
    public void AddDocument(PipelineDocument document) => Selected = AddEditor(document);
    public async Task SaveAsync()
    {
        if (!_loaded || _savedRevision == _revision && File.Exists(_store.FilePath))
            return;
        long revision = _revision;
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
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
    }
    public async Task StopForDeviceAsync(string id, string reason)
    {
        foreach (var editor in Pipelines.Where(p => p.ReferencedDeviceIds.Contains(id)))
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
                Unsubscribe(editor);
            }
        }
        try
        {
            await SaveAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        Pipelines.CollectionChanged -= OnDeviceCollectionChanged;
        if (DevicePane is not null)
            DevicePane.Devices.CollectionChanged -= OnDeviceCollectionChanged;
        _controller.Changed -= OnDevicesChanged;
        L10n.Current.PropertyChanged -= OnLanguageChanged;
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
    private PipelineEditorViewModel AddEditor(PipelineDocument document)
    {
        if (Pipelines.Count >= 32)
            throw new LocalizedOperationException("Error.ModeCount");
        var services = new PipelineServices(_audio);
        var registry = RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry());
        var bindings = SubgraphExpansion.Expand(document).Nodes.SelectMany(n => registry.Get(n.Type).Resources(registry.Get(n.Type).Read(n.Configuration)));
        var assigned = (document.AssignedDevices ?? []).Concat(bindings.Select(b => b.DeviceId).Where(id => !id.StartsWith('$')));
        if (assigned.Any(id => Pipelines.Any(p => p.AssignedDeviceIds.Contains(id))))
            throw new LocalizedOperationException("Error.DeviceInUse");
        var editor = new PipelineEditorViewModel(document, _controller, _waveforms, ReportError, _completed, _timerLimit(), services, registry,
            DevicePane is { } devices ? devices.FlushPreferencesAsync : null);
        editor.CanBindDevice = id => !Pipelines.Any(p => p.Id != editor.Id && p.AssignedDeviceIds.Contains(id));
        editor.GetDefaultOutputLimit = _defaultLimit;
        editor.Session.Changed += OnSessionChanged;
        editor.Edited += OnEdited;
        editor.WaveformCreated += OnWaveformCreated;
        editor.RefreshDevices();
        Pipelines.Add(editor);
        _revision++;
        return editor;
    }
    private void OnWaveformCreated(object? sender, WaveformAsset waveform) => WaveformCreated?.Invoke(this, waveform);
    private void OnDeviceCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshDeviceGroups();
    private void RefreshDeviceGroups()
    {
        var connected = DevicePane?.Devices.Where(d => d.IsConnected).ToArray() ?? [];
        DeviceGroups.Refresh(Pipelines, connected);
    }
    private void Duplicate()
    {
        if (Selected is not { } editor)
            return;
        string name = FormatGeneratedName(editor.Name, stem => L10n.Format("Pipeline.CopyName", stem));
        Selected = AddEditor(Unbind(editor.ToDocument()) with { Id = Guid.NewGuid(), Name = name });
    }
    private static string FormatGeneratedName(string name, Func<string, string> format)
    {
        int length = Math.Min(name.Length, PipelineDocument.MaximumNameLength - format("").Length);
        if (length > 0 && char.IsHighSurrogate(name[length - 1]))
            length--;
        return format(name[..length]);
    }
    private static PipelineDocument Unbind(PipelineDocument document)
    {
        var registry = RemotePipelineNodes.Register(BuiltInNodes.CreateRegistry());
        var bindings = SubgraphExpansion.Expand(document).Nodes.SelectMany(n => registry.Get(n.Type).Resources(registry.Get(n.Type).Read(n.Configuration)))
            .Select(b => b.DeviceId).Where(id => !id.StartsWith('$')).Distinct().Select((id, index) => (id, replacement: "$device" + (index + 1)))
            .ToDictionary(p => p.id, p => p.replacement);
        PipelineDocument ClearAssignments(PipelineDocument graph) => graph with
        {
            AssignedDevices = [],
            Nodes = graph.Nodes.Select(node =>
            {
                var mapped = SubgraphExpansion.MapDevices(node, bindings);
                if (mapped.Type != SubgraphExpansion.Type)
                    return mapped;
                var subgraph = (SubgraphNode)registry.Get(mapped.Type).Read(mapped.Configuration);
                return NodeDefinition.Create(mapped.Type, subgraph with { Graph = ClearAssignments(subgraph.Graph) }, mapped.Id);
            }).ToArray()
        };
        return ClearAssignments(document);
    }
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
        string name = string.IsNullOrWhiteSpace(TemplateName) ? graph.Name : TemplateName;
        if (name.Length > PipelineDocument.MaximumNameLength)
            throw new LocalizedArgumentException("Pipeline.Error.Document");
        var templateAsset = new SavedPipelineTemplate(Guid.NewGuid(), name, PipelineJson.Snapshot(graph), graph.Inputs ?? [], graph.Outputs ?? []);
        await _store.SaveTemplatesAsync(_userTemplates.Append(templateAsset));
        _userTemplates.Add(templateAsset);
        var choice = new PipelineTemplateChoice(savedTemplate: templateAsset);
        Templates.Add(choice);
        SelectedTemplate = choice;
    }
    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is not { IsBuiltIn: false, TemplateId: { } id } selected)
            return;
        await _store.SaveTemplatesAsync(_userTemplates.Where(p => p.Id != id));
        _userTemplates.RemoveAll(p => p.Id == id);
        Templates.Remove(selected);
        SelectedTemplate = Templates.FirstOrDefault();
    }
    private void OnEdited(object? sender, EventArgs e)
    {
        _revision++;
        RefreshDeviceGroups();
        foreach (var pipeline in Pipelines)
            pipeline.RefreshDevices();
        Raise(nameof(CanEditSelected));
        DeleteCommand.NotifyCanExecuteChanged();
        SaveTemplateCommand.NotifyCanExecuteChanged();
    }
    private void OnSessionChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed)
            return;
        RefreshDeviceGroups();
        Raise(nameof(IsAnyRunning));
        Raise(nameof(CanEditSelected));
        DeleteCommand.NotifyCanExecuteChanged();
        SaveTemplateCommand.NotifyCanExecuteChanged();
    });
    private void OnDevicesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed)
            return;
        foreach (var pipeline in Pipelines)
            pipeline.RefreshDevices();
        RefreshDeviceGroups();
    });
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(JsonLocalizer.Language))
            return;
        RefreshDeviceGroups();
        foreach (var template in Templates)
            template.RefreshLocalization();
        foreach (var pipeline in Pipelines)
            pipeline.RefreshLocalization();
    }
    private void Unsubscribe(PipelineEditorViewModel editor)
    {
        editor.Session.Changed -= OnSessionChanged;
        editor.Edited -= OnEdited;
        editor.WaveformCreated -= OnWaveformCreated;
    }
}
