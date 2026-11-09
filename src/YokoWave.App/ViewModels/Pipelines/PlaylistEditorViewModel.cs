using System.Collections.ObjectModel;
using YokoWave.Core;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PlaylistEditorViewModel : NodeEditorViewModel, IWaveformNodeEditor
{
    private PlaylistNode _settings;
    private WaveformItem? _selectedEntry;
    public double Phase
    {
        get => _settings.Phase;
        set { if (double.IsFinite(value)) Change(() => _settings = _settings with { Phase = value }); }
    }
    public bool RestartOnTrigger { get => _settings.RestartOnTrigger; set => Change(() => _settings = _settings with { RestartOnTrigger = value }); }
    public bool StopWhenComplete { get => _settings.StopWhenComplete; set => Change(() => _settings = _settings with { StopWhenComplete = value }); }
    public ObservableCollection<WaveformItem> Waveforms => Editor.Waveforms;
    public ObservableCollection<WaveformItem> Playlist { get; } = [];
    public WaveformItem? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (Set(ref _selectedEntry, value))
                RefreshCollectionCommands();
        }
    }
    public IReadOnlyList<SessionOption> Orders { get; } = Enum.GetValues<PlaybackOrder>().Select(o => new SessionOption((int)o, "Playback." + o)).ToArray();
    public WaveformItem? SelectedWaveform
    {
        get => Waveforms.FirstOrDefault(p => p.Asset.Id == _settings.WaveformId);
        set
        {
            if (value is null || value.Asset.Id == _settings.WaveformId)
                return;
            if (!CanEdit && !_settings.AcceptsWaveform(value.Asset.Id))
                return;
            Change(() =>
            {
                _settings = _settings with { WaveformId = value.Asset.Id };
                Raise();
            }, live: true);
            Editor.Session.Update(NodeControl.Create(Id, "waveform", value.Asset));
        }
    }
    public SessionOption SelectedOrder
    {
        get => Orders.Single(o => o.Value == (int)_settings.Order);
        set
        {
            if (value is not null)
            {
                Change(() =>
                {
                    _settings = _settings with { Order = (PlaybackOrder)value.Value };
                    Raise();
                });
            }
        }
    }
    public override string Summary => Preview?.Name ?? "";
    public override WaveformItem? Preview => Editor.Session.HoldsResources && Editor.Session.NodeStatus.GetValueOrDefault(Id)?.Waveform is { } id
        ? Waveforms.FirstOrDefault(p => p.Asset.Id == id) : SelectedWaveform;
    public ActionCommand AddWaveCommand { get; }
    public ActionCommand RemoveWaveCommand { get; }
    public ActionCommand MoveUpCommand { get; }
    public ActionCommand MoveDownCommand { get; }
    public PlaylistEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        _settings = (PlaylistNode)editor.Registry.Get(Type).Read(definition.Configuration);
        foreach (var id in _settings.Playlist ?? [])
        {
            if (Waveforms.FirstOrDefault(p => p.Asset.Id == id) is { } waveform)
                Playlist.Add(waveform);
        }

        AddWaveCommand = new ActionCommand(() => Change(() =>
        {
            if (SelectedWaveform is { } selected && Playlist.Count < 100 && Playlist.All(p => p.Asset.Id != selected.Asset.Id))
            {
                Playlist.Add(selected);
                SelectedEntry = selected;
            }
            SavePlaylist();
        }), () => CanEdit && SelectedWaveform is { } waveform && Playlist.Count < 100 && Playlist.All(p => p.Asset.Id != waveform.Asset.Id));
        RemoveWaveCommand = new ActionCommand(() => Change(() =>
        {
            if (SelectedEntry is not { } entry)
                return;
            int index = Playlist.IndexOf(entry);
            Playlist.Remove(entry);
            SelectedEntry = Playlist.ElementAtOrDefault(Math.Min(index, Playlist.Count - 1));
            SavePlaylist();
        }), () => CanEdit && SelectedEntry is { } entry && Playlist.Contains(entry));
        MoveUpCommand = new ActionCommand(() => Move(-1), () => CanMove(-1));
        MoveDownCommand = new ActionCommand(() => Move(1), () => CanMove(1));
        Playlist.CollectionChanged += (_, _) => RefreshCollectionCommands();
    }
    public override bool IsPortAvailable(NodePort port) => WaveformPorts.IsAvailable(port, Preview?.Asset);
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _settings, Id);
    public bool SelectWaveform(WaveformItem waveform)
    {
        SelectedWaveform = waveform;
        return SelectedWaveform?.Asset.Id == waveform.Asset.Id;
    }
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var order in Orders)
            order.RefreshLocalization();
        RefreshCollectionCommands();
    }
    private bool CanMove(int offset) => CanEdit && SelectedEntry is { } entry && Playlist.IndexOf(entry) is var index &&
        index >= 0 && index + offset >= 0 && index + offset < Playlist.Count;
    private void RefreshCollectionCommands()
    {
        AddWaveCommand.NotifyCanExecuteChanged();
        RemoveWaveCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
    private void Move(int offset) => Change(() =>
    {
        if (SelectedEntry is not { } entry)
            return;
        int index = Playlist.IndexOf(entry);
        if (index + offset >= 0 && index + offset < Playlist.Count)
        {
            Playlist.Move(index, index + offset);
            SelectedEntry = entry;
        }
        SavePlaylist();
    });
    private void SavePlaylist() => _settings = _settings with { Playlist = Playlist.Select(p => p.Asset.Id).ToArray() };
}
