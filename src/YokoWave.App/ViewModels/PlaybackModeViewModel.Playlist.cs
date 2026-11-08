using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using YokoWave.Core.Localization;

namespace YokoWave.App.ViewModels;

public sealed partial class PlaybackModeViewModel
{
    private PresetItem? _playlistCandidate;
    private bool _refreshingWaveformChoices;
    public ObservableCollection<PlaylistEntry> Playlist { get; } = [];
    public bool SupportsPlaylist => Definition.SupportsPlaylist;
    public IReadOnlyList<PresetItem> PlaybackPresets => UsesCustomPlaylist ? Playlist.Select(entry => entry.Preset).ToArray()
        : Presets;
    public string PlaylistHelp => L10n.Get(IsSleep ? "Playlist.SleepHelp" : "Playlist.Help");
    public System.Windows.Input.ICommand WavesCommand => _host.WavesCommand;
    public IReadOnlyList<PresetItem> PlaylistCandidates => Presets;
    public PresetItem? PlaylistCandidate
    {
        get => _playlistCandidate ?? PlaylistCandidates.FirstOrDefault();
        set { Set(ref _playlistCandidate, value); AddPlaylistCommand?.NotifyCanExecuteChanged(); }
    }
    public bool HasPlaylist => Playlist.Count > 0;
    public AsyncCommand AddPlaylistCommand { get; private set; }
    private bool CanAddPlaylist => CanEditRouting && SupportsPlaylist && Playlist.Count < 100 &&
        PlaylistCandidate is { } candidate && Playlist.All(entry => entry.Id != candidate.Preset.Id);

    [MemberNotNull(nameof(AddPlaylistCommand))]
    private void InitializePlaylist(List<Guid>? ids)
    {
        AddPlaylistCommand = new(() => { AddPlaylistWave(); return Task.CompletedTask; }, ShowError, () => CanAddPlaylist);
        if (!SupportsPlaylist) return;
        foreach (var id in ids ?? (SelectedPreset is { } preset ? [preset.Preset.Id] : []))
            if (Presets.FirstOrDefault(item => item.Preset.Id == id) is { } item) Playlist.Add(new(this, item));
        RefreshPlaylist();
    }
    public void AddPlaylistWave()
    {
        if (!CanAddPlaylist || PlaylistCandidate is not { } candidate) return;
        Playlist.Add(new(this, candidate)); RefreshPlaylist();
    }
    internal void RemovePlaylistWave(Guid id)
    {
        if (!CanEditRouting) return;
        foreach (var item in Playlist.Where(item => item.Id == id).ToArray()) Playlist.Remove(item);
        RefreshPlaylist();
    }
    internal void MovePlaylistWave(PlaylistEntry item, int direction)
    {
        if (!CanEditRouting) return;
        var from = Playlist.IndexOf(item); var to = from + direction;
        if (from < 0 || to < 0 || to >= Playlist.Count) return;
        Playlist.Move(from, to); RefreshPlaylist();
    }
    public void RefreshPlaylist()
    {
        _refreshingWaveformChoices = true;
        try { RefreshPlaylistCore(); }
        finally { _refreshingWaveformChoices = false; }
    }
    private void RefreshPlaylistCore()
    {
        foreach (var item in Playlist) item.Refresh();
        if (_playlistCandidate is { } candidate && !PlaylistCandidates.Contains(candidate)) _playlistCandidate = null;
        Raise(nameof(PlaylistCandidates)); Raise(nameof(PlaylistCandidate)); Raise(nameof(HasPlaylist));
        if (UsesCustomPlaylist && (Playlist.Count == 0 || Playlist.All(entry => entry.Id != SelectedPreset?.Preset.Id))) SelectedPreset = Playlist.FirstOrDefault()?.Preset;
        Raise(nameof(PlaybackPresets)); Raise(nameof(SelectedPreset)); Raise(nameof(PlaylistHelp)); Raise(nameof(CanStart));
        RefreshPlaylistCommands();
    }
    private void RefreshPlaylistCommands()
    {
        AddPlaylistCommand?.NotifyCanExecuteChanged();
        foreach (var item in Playlist) item.RefreshCommands();
    }
}

public sealed class PlaylistEntry : ObservableObject
{
    private readonly PlaybackModeViewModel _mode;
    private PresetItem _preset;
    public Guid Id { get; }
    public PresetItem Preset => _preset;
    public string Name => _preset.Name;
    public string Detail => _preset.Detail;
    public int Number => _mode.Playlist.IndexOf(this) + 1;
    public AsyncCommand MoveUpCommand { get; }
    public AsyncCommand MoveDownCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public PlaylistEntry(PlaybackModeViewModel mode, PresetItem preset)
    {
        _mode = mode; _preset = preset; Id = preset.Preset.Id;
        MoveUpCommand = new(() => Move(-1), mode.ReportError, () => mode.CanEditRouting && Number > 1);
        MoveDownCommand = new(() => Move(1), mode.ReportError, () => mode.CanEditRouting && Number < mode.Playlist.Count);
        RemoveCommand = new(() => { mode.RemovePlaylistWave(Id); return Task.CompletedTask; }, mode.ReportError, () => mode.CanEditRouting);
    }
    private Task Move(int direction) { _mode.MovePlaylistWave(this, direction); return Task.CompletedTask; }
    public void Refresh()
    {
        _preset = _mode.Presets.FirstOrDefault(item => item.Preset.Id == Id) ?? _preset;
        Raise(nameof(Name)); Raise(nameof(Detail)); Raise(nameof(Number));
    }
    public void RefreshCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged(); MoveDownCommand.NotifyCanExecuteChanged(); RemoveCommand.NotifyCanExecuteChanged();
    }
}
