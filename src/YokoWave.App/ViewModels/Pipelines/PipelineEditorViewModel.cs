using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Windows.Input;
using Avalonia.Threading;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PipelinePaletteItem(NodeRegistration registration, Action add) : ObservableObject
{
    public string Title => L10n.Get(registration.NameKey);
    public ICommand AddCommand { get; } = new ActionCommand(add);
    public void RefreshLocalization() => Raise(nameof(Title));
}

public sealed class PipelineParameterCommand<T>(Action<T> action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T;
    public void Execute(object? parameter)
    {
        if (parameter is T value)
            action(value);
    }
}

public sealed class PipelineEditorViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceController _controller;
    private readonly Action<SessionRecord> _completed;
    private readonly ConcurrentQueue<SessionRecord> _completedRecords = new();
    private readonly PipelineCompiler _compiler;
    private readonly PipelineNodeCatalog _catalog = new();
    public Action<Exception> ReportError { get; }
    public IAudioInputFactory AudioInputs { get; }
    public PipelineServices Services { get; }
    internal Func<string, bool>? CanBindDevice { get; set; }
    public ObservableCollection<PipelineEndpointViewModel> Endpoints { get; } = [];
    public ObservableCollection<PipelineBindingViewModel> Bindings { get; } = [];
    public event EventHandler<WaveformPreset>? WaveformCreated;
    private readonly List<PipelineDocument> _undo = [];
    private readonly List<PipelineDocument> _redo = [];
    private PipelineDocument? _clipboard;
    private bool _restoring;
    private string _name;
    private double _outputLimit, _durationSeconds, _timerLimitSeconds;
    private bool _timerEnabled;
    private bool _disposed;
    private bool _showPalette = true, _showInspector = true;
    public bool ShowPalette { get => _showPalette; set => Set(ref _showPalette, value); }
    public bool ShowInspector { get => _showInspector; set => Set(ref _showInspector, value); }
    public NodeTypeRegistry Registry { get; }
    public PipelineSession Session { get; }
    public Guid Id { get; }
    public ObservableCollection<PresetItem> Presets { get; }
    public ObservableCollection<PipelineDeviceOption> Devices { get; } = [];
    public ObservableCollection<NodeEditorViewModel> Nodes { get; } = [];
    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<NodeEditorViewModel> SelectedNodes { get; } = [];
    public ObservableCollection<PipelinePaletteItem> Palette { get; } = [];
    private string _search = "";
    public string Search
    {
        get => _search; set
        {
            if (Set(ref _search, value))
                Raise(nameof(FilteredPalette));
        }
    }
    public IEnumerable<PipelinePaletteItem> FilteredPalette => Palette.Where(p => p.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
    public ObservableCollection<PipelineDiagnostic> Diagnostics { get; } = [];
    public object PendingConnection { get; } = new();
    public NodeEditorViewModel? SelectedNode => SelectedNodes.FirstOrDefault();
    public bool HasSelectedNode => SelectedNode is not null;
    public bool CanEdit => !Session.HoldsResources && !Nodes.OfType<AudioNodeEditorViewModel>().Any(n => n.IsCalibrating);
    public bool CanUndo => CanEdit && _undo.Count > 0 && BindingsAvailable(_undo[^1]);
    public bool CanRedo => CanEdit && _redo.Count > 0 && BindingsAvailable(_redo[^1]);
    public bool CanStart => CanEdit && Diagnostics.Count == 0 && BoundDeviceIds.All(id =>
        CanBindDevice?.Invoke(id) != false && _controller.Connections.ContainsKey(id) && !_controller.IsDeviceBusy(id));
    public bool CanStop => Session.HoldsResources;
    public bool CanPause => Session.State is PipelineState.Running or PipelineState.Paused;
    public string StateLabel => L10n.Get($"Pipeline.State.{Session.State}");
    public string PauseLabel => L10n.Get(Session.State == PipelineState.Paused ? "Pipeline.Resume" : "Pipeline.Pause");
    public bool ShowTimerProgress => !CanEdit && TimerEnabled;
    public bool CanEditDuration => CanEdit && TimerEnabled;
    public double TimerProgress => Math.Clamp((Session.Remaining?.TotalSeconds ?? DurationSeconds) / DurationSeconds, 0, 1);
    public string TimerReadout => TimerEnabled ? TimeSpan.FromSeconds(Math.Ceiling(CanEdit ? DurationSeconds : Session.Remaining?.TotalSeconds ?? DurationSeconds))
        .ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss") : L10n.Get("Session.NoTimer");
    public string BoundDevices => string.Join(", ", BoundDeviceIds.Select(id => Devices.FirstOrDefault(d => d.Id == id)?.Name ?? id));
    public IReadOnlyList<string> BoundDeviceIds => GetBoundDevices();
    private IReadOnlyList<string> GetBoundDevices()
    {
        try
        {
            return SubgraphExpansion.Expand(ToDocument()).Nodes.SelectMany(n => Registry.Get(n.Type).Resources(Registry.Get(n.Type).Read(n.Configuration)))
                .Select(b => b.DeviceId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or PipelineValidationException or KeyNotFoundException or ArgumentException)
        {
            return [];
        }
    }
    public string InputStatus => Session.Error?.Message ?? (Session.LastFrame?.Diagnostics.Count > 0 ? L10n.Get("Pipeline.Error.Input") : "");
    public string Name
    {
        get => _name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 60 && value != _name)
                Edit(() => Set(ref _name, value));
        }
    }
    public double OutputLimit
    {
        get => _outputLimit;
        set
        {
            if (double.IsFinite(value) && value is >= 0 and <= 1 && value != _outputLimit)
            {
                Edit(() => Set(ref _outputLimit, value), live: true);
                Session.SetLimit(value);
            }
        }
    }
    public bool TimerEnabled
    {
        get => _timerEnabled;
        set
        {
            if (value != _timerEnabled)
                Edit(() => Set(ref _timerEnabled, value));
        }
    }
    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (double.IsFinite(value) && value >= 1 && value <= _timerLimitSeconds && value != _durationSeconds)
                Edit(() => Set(ref _durationSeconds, Math.Truncate(value)));
        }
    }
    public double TimerLimitSeconds => _timerLimitSeconds;
    public ICommand CreateConnectionCommand { get; }
    public ICommand RemoveConnectionCommand { get; }
    public ICommand DisconnectConnectorCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand PauseCommand { get; }
    public ICommand DeleteSelectionCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public event EventHandler? Edited;

    public PipelineEditorViewModel(PipelineDocument document, DeviceController controller, ObservableCollection<PresetItem> presets,
        Action<Exception> onError, Action<SessionRecord> completed, double timerLimitSeconds = 7200, PipelineServices? services = null, NodeTypeRegistry? registry = null)
    {
        Id = document.Id;
        _name = document.Name;
        _controller = controller;
        _completed = completed;
        Presets = presets;
        Registry = registry ?? BuiltInNodes.CreateRegistry();
        ReportError = onError;
        AudioInputs = services?.Audio ?? new UnavailableAudioInputFactory();
        Services = services ?? new(AudioInputs);
        _compiler = new(Registry);
        _timerLimitSeconds = timerLimitSeconds;
        Session = new(controller, Services);
        Session.Changed += OnSessionChanged;
        Session.Completed += OnCompleted;
        CreateConnectionCommand = new PipelineParameterCommand<(object Source, object? Target)>(pair =>
        {
            if (pair.Source is NodePortViewModel a && pair.Target is NodePortViewModel b)
                Connect(a, b);
        });
        RemoveConnectionCommand = new PipelineParameterCommand<ConnectionViewModel>(connection => Edit(() => Connections.Remove(connection)));
        DisconnectConnectorCommand = new PipelineParameterCommand<NodePortViewModel>(port => Edit(() =>
        {
            foreach (var connection in Connections.Where(c => c.Source == port || c.Target == port).ToArray())
                Connections.Remove(connection);
        }));
        StartCommand = new(StartAsync, onError, () => CanStart);
        StopCommand = new(() => StopAsync(), onError, () => CanStop);
        PauseCommand = new(() => Session.PauseAsync(), onError, () => CanPause);
        DeleteSelectionCommand = new ActionCommand(DeleteSelection);
        CopyCommand = new ActionCommand(CopySelection);
        PasteCommand = new ActionCommand(Paste);
        UndoCommand = new ActionCommand(() => RestoreHistory(_undo, _redo));
        RedoCommand = new ActionCommand(() => RestoreHistory(_redo, _undo));
        foreach (var registration in Registry.Types)
            Palette.Add(new(registration, () => AddNode(registration.Type)));
        SelectedNodes.CollectionChanged += (_, _) => { Raise(nameof(SelectedNode)); Raise(nameof(HasSelectedNode)); };
        Restore(document);
    }

    public PipelineDocument ToDocument() => new(Id, Name, Nodes.Select(n => n.ToDefinition()).ToArray(),
        Connections.Select(c => c.Definition).ToArray(), Nodes.Select(n => new NodeLayout(n.Id, n.Location.X, n.Location.Y)).ToArray(),
        OutputLimit, DurationSeconds, TimerEnabled, Inputs: Nodes.SelectMany(n => n.Inputs).Where(p => p.IsExposed)
            .Select(p => new ExposedPort(p.Node.Id.ToString("N") + "." + p.Port.Id, p.Port.NameKey, new(p.Node.Id, p.Port.Id), p.Port.Parameter, p.Port.Kind)).ToArray(),
        Outputs: Nodes.SelectMany(n => n.Outputs).Where(p => p.IsExposed)
            .Select(p => new ExposedPort(p.Node.Id.ToString("N") + "." + p.Port.Id, p.Port.NameKey, new(p.Node.Id, p.Port.Id), p.Port.Parameter, p.Port.Kind)).ToArray());
    public void MarkEdited()
    {
        if (!_restoring)
            Edited?.Invoke(this, EventArgs.Empty);
    }
    public void AddRecording(WaveformPreset preset) => WaveformCreated?.Invoke(this, preset);
    public bool UsesWaveform(Guid id) => SubgraphExpansion.Expand(ToDocument()).Nodes.Any(n =>
        Registry.Get(n.Type).WaveformReferences(Registry.Get(n.Type).Read(n.Configuration)).Contains(id));
    public bool CanUseWaveform => Nodes.Any(n => n is IWaveformNodeEditor);
    public bool UseWaveform(WaveformPreset preset)
    {
        var node = SelectedNode as IWaveformNodeEditor ?? Nodes.OfType<IWaveformNodeEditor>().FirstOrDefault();
        var item = Presets.FirstOrDefault(p => p.Preset.Id == preset.Id);
        return item is not null && node?.SelectWaveform(item) == true;
    }
    public void RebindDevice(string current, string replacement) => Edit(() =>
    {
        var document = ToDocument();
        Restore(document with
        {
            Nodes = document.Nodes.Select(n => SubgraphExpansion.MapDevices(n, new Dictionary<string, string> { [current] = replacement })).ToArray()
        });
    });
    public void ReplaceNode(NodeDefinition replacement)
    {
        if (!CanEdit)
            return;
        var document = ToDocument();
        var candidate = document with
        {
            Nodes = document.Nodes.Select(n => n.Id == replacement.Id ? replacement : n).ToArray()
        };
        if (_compiler.Validate(candidate, Presets.Select(p => p.Preset)).Any(d => d.Key is "Pipeline.Error.Connection" or "Pipeline.Error.Cycle"))
            throw new Core.Localization.LocalizedOperationException("Pipeline.Error.Connection");
        Edit(() => Restore(candidate));
    }
    public void ExpandSubgraphs() => Edit(() => Restore(SubgraphExpansion.Expand(ToDocument())));

    public void Edit(Action mutation, bool layoutOnly = false, bool live = false)
    {
        if (_restoring || _disposed || !CanEdit && !layoutOnly && !live)
            return;
        var before = ToDocument();
        mutation();
        if (!layoutOnly && BoundDeviceIds.Any(id => !id.StartsWith('$') && CanBindDevice?.Invoke(id) == false))
        {
            Restore(before);
            throw new Core.Localization.LocalizedOperationException("Error.DeviceInUse");
        }
        if (CanEdit)
        {
            _undo.Add(before);
            if (_undo.Count > 100)
                _undo.RemoveAt(0);
            _redo.Clear();
        }
        Validate();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    public NodeEditorViewModel AddNode(string type)
    {
        var node = _catalog.Create(this, _catalog.Default(this, type));
        Edit(() =>
        {
            node.RestoreLocation(new(80 + Nodes.Count % 3 * 260, 80 + Nodes.Count / 3 * 220));
            Nodes.Add(node);
        });
        return node;
    }

    public bool Connect(NodePortViewModel a, NodePortViewModel b)
    {
        if (!CanEdit || !Nodes.Contains(a.Node) || !Nodes.Contains(b.Node))
            return false;
        var source = a.Port.Direction == PortDirection.Output ? a : b;
        var target = source == a ? b : a;
        if (source.Port.Direction != PortDirection.Output || target.Port.Direction != PortDirection.Input || source.Port.Kind != target.Port.Kind || source.Node == target.Node ||
            Connections.Any(c => c.Target == target))
            return false;
        var candidate = ToDocument() with
        {
            Connections = [.. Connections.Select(c => c.Definition), new(new(source.Node.Id, source.Port.Id), new(target.Node.Id, target.Port.Id))]
        };
        if (_compiler.Validate(candidate, Presets.Select(p => p.Preset)).Any(d => d.Key == "Pipeline.Error.Cycle"))
            return false;
        Edit(() => Connections.Add(new(source, target)));
        return true;
    }

    private void DeleteSelection() => Edit(() =>
    {
        var selected = SelectedNodes.ToHashSet();
        foreach (var connection in Connections.Where(c => selected.Contains(c.Source.Node) || selected.Contains(c.Target.Node)).ToArray())
            Connections.Remove(connection);
        foreach (var node in selected)
            Nodes.Remove(node);
        SelectedNodes.Clear();
    });

    private void CopySelection()
    {
        var selected = SelectedNodes.Select(n => n.Id).ToHashSet();
        var document = ToDocument();
        _clipboard = document with
        {
            Nodes = document.Nodes.Where(n => selected.Contains(n.Id)).ToArray(),
            Connections = document.Connections.Where(c => selected.Contains(c.Source.Node) && selected.Contains(c.Target.Node)).ToArray(),
            Layout = document.Layout.Where(l => selected.Contains(l.Node)).ToArray()
        };
    }

    private void Paste()
    {
        if (_clipboard is not { Nodes.Length: > 0 } copied)
            return;
        Edit(() =>
        {
            var ids = copied.Nodes.ToDictionary(n => n.Id, _ => Guid.NewGuid());
            var current = ToDocument();
            Restore(current with
            {
                Nodes = [.. current.Nodes, .. copied.Nodes.Select(n => n with { Id = ids[n.Id] })],
                Connections = [.. current.Connections, .. copied.Connections.Select(c => new PipelineConnection(new(ids[c.Source.Node], c.Source.Port), new(ids[c.Target.Node], c.Target.Port)))],
                Layout = [.. current.Layout, .. copied.Layout.Select(l => new NodeLayout(ids[l.Node], l.X + 40, l.Y + 40))]
            });
        });
    }

    private void RestoreHistory(List<PipelineDocument> from, List<PipelineDocument> to)
    {
        if (!CanEdit || from.Count == 0 || !BindingsAvailable(from[^1]))
            return;
        to.Add(ToDocument());
        var previous = from[^1];
        from.RemoveAt(from.Count - 1);
        Restore(previous);
        Edited?.Invoke(this, EventArgs.Empty);
    }
    private bool BindingsAvailable(PipelineDocument document) => SubgraphExpansion.Expand(document).Nodes.SelectMany(n =>
        Registry.Get(n.Type).Resources(Registry.Get(n.Type).Read(n.Configuration))).All(b => b.DeviceId.StartsWith('$') || CanBindDevice?.Invoke(b.DeviceId) != false);

    private void Restore(PipelineDocument document)
    {
        _restoring = true;
        try
        {
            _name = document.Name;
            _outputLimit = document.OutputLimit;
            _timerEnabled = document.TimerEnabled;
            _durationSeconds = Math.Min(document.DurationSeconds, _timerLimitSeconds);
            SelectedNodes.Clear();
            Connections.Clear();
            Nodes.Clear();
            foreach (var definition in document.Nodes)
            {
                var node = _catalog.Create(this, definition);
                if (document.Layout.FirstOrDefault(l => l.Node == definition.Id) is { } layout)
                    node.RestoreLocation(new(layout.X, layout.Y));
                Nodes.Add(node);
                foreach (var port in node.Inputs)
                    port.IsExposed = document.Inputs?.Any(p => p.Address.Node == node.Id && p.Address.Port == port.Port.Id) == true;
                foreach (var port in node.Outputs)
                    port.IsExposed = document.Outputs?.Any(p => p.Address.Node == node.Id && p.Address.Port == port.Port.Id) == true;
            }
            foreach (var connection in document.Connections)
            {
                var source = Nodes.Single(n => n.Id == connection.Source.Node).Outputs.Single(p => p.Port.Id == connection.Source.Port);
                var target = Nodes.Single(n => n.Id == connection.Target.Node).Inputs.Single(p => p.Port.Id == connection.Target.Port);
                Connections.Add(new(source, target));
            }
        }
        finally { _restoring = false; }
        foreach (var property in new[] { nameof(Name), nameof(OutputLimit), nameof(TimerEnabled), nameof(DurationSeconds) })
            Raise(property);
        Validate();
    }

    public void Validate()
    {
        Diagnostics.Clear();
        foreach (var issue in _compiler.Validate(ToDocument(), Presets.Select(p => p.Preset)))
            Diagnostics.Add(issue);
        foreach (var native in Nodes.OfType<NativeOutputEditorViewModel>().Where(n => n.ValidationError.Length > 0))
            Diagnostics.Add(new("Error.ProtocolSettings", native.Id));
        foreach (var port in Nodes.SelectMany(n => n.Inputs.Concat(n.Outputs)))
            port.IsConnected = Connections.Any(c => c.Source == port || c.Target == port);
        RefreshState();
    }

    public DeviceDescriptor? FindDevice(string id) => _controller.Connections.GetValueOrDefault(id)?.Device;

    public void RefreshDevices()
    {
        var descriptors = _controller.Connections.Values.Select(c => c.Device).ToArray();
        var desired = descriptors.Where(d => CanBindDevice?.Invoke(d.Id) != false).Select(d => new PipelineDeviceOption(d.Id, d.Name))
            .Concat(BoundDeviceIds.Where(id => descriptors.All(d => d.Id != id)).Select(id => new PipelineDeviceOption(id, id)))
            .Prepend(new("$device", L10n.Get("Pipeline.Unbound"))).DistinctBy(d => d.Id).ToArray();
        if (!Devices.SequenceEqual(desired))
        {
            Devices.Clear();
            foreach (var option in desired)
                Devices.Add(option);
            foreach (var node in Nodes.OfType<DeviceOutputEditorViewModel>())
                node.RefreshDevices();
        }
        RefreshState();
        var bindings = BoundDeviceIds;
        if (!Bindings.Select(b => b.Id).SequenceEqual(bindings))
        {
            Bindings.Clear();
            foreach (var id in bindings)
                Bindings.Add(new(this, id));
        }
    }

    public void SetTimerLimit(double seconds)
    {
        if (!CanEdit)
            return;
        _timerLimitSeconds = seconds;
        _durationSeconds = Math.Min(_durationSeconds, seconds);
        Raise(nameof(TimerLimitSeconds));
        Raise(nameof(DurationSeconds));
        RefreshState();
    }

    public async Task StartAsync()
    {
        if (!BindingsAvailable(ToDocument()))
            throw new Core.Localization.LocalizedOperationException("Error.DeviceInUse");
        if (Nodes.OfType<NativeOutputEditorViewModel>().Any(n => n.ValidationError.Length > 0))
            throw new Core.Localization.LocalizedOperationException("Error.ProtocolSettings");
        var plan = _compiler.Compile(ToDocument(), Presets.Select(p => p.Preset), _controller.Connections.ToDictionary(p => p.Key, p => p.Value.Device));
        await Session.StartAsync(plan);
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
        {
            FlushRecords();
            RefreshState();
        }
    });

    private void OnCompleted(object? sender, SessionRecord record) => _completedRecords.Enqueue(record);
    private void FlushRecords()
    {
        while (_completedRecords.TryDequeue(out var record))
            _completed(record);
    }
    public async Task StopAsync(string reason = "Reason.Manual")
    {
        await Session.StopAsync(reason);
        FlushRecords();
    }

    private void RefreshState()
    {
        foreach (var property in new[] { nameof(CanEdit), nameof(CanUndo), nameof(CanRedo), nameof(CanStart), nameof(CanStop), nameof(CanPause),
            nameof(StateLabel), nameof(PauseLabel), nameof(ShowTimerProgress), nameof(CanEditDuration), nameof(TimerProgress), nameof(TimerReadout), nameof(BoundDevices), nameof(InputStatus) })
            Raise(property);
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        var endpoints = Services.Endpoints.Select(p => new PipelineEndpointViewModel(p.Value.Url, p.Value.Kind,
            Services.Credentials.GetValueOrDefault(p.Key)?.Token ?? "")).ToArray();
        if (!Endpoints.SequenceEqual(endpoints))
        {
            Endpoints.Clear();
            foreach (var endpoint in endpoints)
                Endpoints.Add(endpoint);
        }
        foreach (var node in Nodes)
            node.RefreshLocalization();
    }

    public void RefreshLocalization()
    {
        foreach (var item in Palette)
            item.RefreshLocalization();
        Raise(nameof(FilteredPalette));
        Validate();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        try
        {
            List<Exception> errors = [];
            try
            {
                await Session.DisposeAsync();
            }
            catch (Exception error) { errors.Add(error); }
            foreach (var node in Nodes.OfType<IAsyncDisposable>())
            {
                try
                {
                    await node.DisposeAsync();
                }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count > 0)
                throw new Core.Localization.LocalizedAggregateException("Error.Cleanup", errors);
            FlushRecords();
        }
        finally
        {
            _disposed = true;
            Session.Changed -= OnSessionChanged;
            Session.Completed -= OnCompleted;
        }
    }
}

public sealed record PipelineEndpointViewModel(string Url, string Kind, string Token);
public sealed class PipelineBindingViewModel(PipelineEditorViewModel editor, string id) : ObservableObject
{
    public string Id { get; } = id;
    public ObservableCollection<PipelineDeviceOption> Devices => editor.Devices;
    public PipelineDeviceOption? Selected
    {
        get => Devices.FirstOrDefault(d => d.Id == Id);
        set
        {
            if (value is not null && value.Id != Id)
                editor.RebindDevice(Id, value.Id);
        }
    }
}
