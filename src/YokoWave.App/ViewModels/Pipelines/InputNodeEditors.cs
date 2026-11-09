using System.Collections.ObjectModel;
using System.Windows.Input;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

public sealed class PlaylistEditorViewModel : NodeEditorViewModel, IWaveformNodeEditor
{
    public double Phase { get => _settings.Phase; set { if (double.IsFinite(value)) Change(() => _settings = _settings with { Phase = value }); } }
    public bool RestartOnTrigger { get => _settings.RestartOnTrigger; set => Change(() => _settings = _settings with { RestartOnTrigger = value }); }
    public bool StopWhenComplete { get => _settings.StopWhenComplete; set => Change(() => _settings = _settings with { StopWhenComplete = value }); }
    private PlaylistNode _settings;
    private PresetItem? _selectedEntry;
    public ObservableCollection<PresetItem> Presets => Editor.Presets;
    public ObservableCollection<PresetItem> Playlist { get; } = [];
    public PresetItem? SelectedEntry { get => _selectedEntry; set => Set(ref _selectedEntry, value); }
    public IReadOnlyList<SessionOption> Orders { get; } = Enum.GetValues<PlaybackOrder>().Select(o => new SessionOption((int)o, "Playback." + o)).ToArray();
    public PresetItem? SelectedPreset
    {
        get => Presets.FirstOrDefault(p => p.Preset.Id == _settings.WaveformId);
        set
        {
            if (value is null || value.Preset.Id == _settings.WaveformId)
                return;
            if (!CanEdit && !_settings.AcceptsWaveform(value.Preset.Id))
                return;
            Change(() => { _settings = _settings with { WaveformId = value.Preset.Id }; Raise(); }, live: true);
            Editor.Session.Update(NodeControl.Create(Id, "waveform", value.Preset));
        }
    }
    public SessionOption SelectedOrder
    {
        get => Orders.Single(o => o.Value == (int)_settings.Order);
        set
        {
            if (value is not null)
                Change(() => { _settings = _settings with { Order = (PlaybackOrder)value.Value }; Raise(); });
        }
    }
    public override string Summary => Preview?.Name ?? "";
    public override PresetItem? Preview => Editor.Session.HoldsResources && Editor.Session.NodeStatus.GetValueOrDefault(Id)?.Waveform is { } id
        ? Presets.FirstOrDefault(p => p.Preset.Id == id) : SelectedPreset;
    public override bool IsPortAvailable(NodePort port) => WaveformPorts.IsAvailable(port, Preview?.Preset);
    public ICommand AddWaveCommand { get; }
    public ICommand RemoveWaveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public PlaylistEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        _settings = (PlaylistNode)editor.Registry.Get(Type).Read(definition.Configuration);
        foreach (var id in _settings.Playlist ?? [])
            if (Presets.FirstOrDefault(p => p.Preset.Id == id) is { } preset)
                Playlist.Add(preset);
        AddWaveCommand = new ActionCommand(() => Change(() =>
        {
            if (SelectedPreset is { } selected && Playlist.Count < 100 && Playlist.All(p => p.Preset.Id != selected.Preset.Id))
                Playlist.Add(selected);
            SavePlaylist();
        }));
        RemoveWaveCommand = new ActionCommand(() => Change(() => { if (SelectedEntry is { } entry) Playlist.Remove(entry); SavePlaylist(); }));
        MoveUpCommand = new ActionCommand(() => Move(-1));
        MoveDownCommand = new ActionCommand(() => Move(1));
    }
    private void Move(int offset) => Change(() =>
    {
        if (SelectedEntry is not { } entry)
            return;
        var index = Playlist.IndexOf(entry);
        if (index + offset >= 0 && index + offset < Playlist.Count)
            Playlist.Move(index, index + offset);
        SavePlaylist();
    });
    private void SavePlaylist() => _settings = _settings with { Playlist = Playlist.Select(p => p.Preset.Id).ToArray() };
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _settings, Id);
    public bool SelectWaveform(PresetItem preset)
    {
        SelectedPreset = preset;
        return SelectedPreset?.Preset.Id == preset.Preset.Id;
    }
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var order in Orders)
            order.RefreshLocalization();
    }
}

public sealed class TouchNodeEditorViewModel : NodeEditorViewModel
{
    private readonly TouchNode _settings;
    private double _frequency = 20, _width = 50;
    private bool _held;
    private string _recordingName = "";
    public double Frequency
    {
        get => _frequency; set
        {
            if (double.IsFinite(value) && Set(ref _frequency, Math.Clamp(value, 1, 100)))
                Send();
        }
    }
    public double PulseWidth
    {
        get => _width; set
        {
            if (double.IsFinite(value) && Set(ref _width, Math.Clamp(value, 0, 100)))
                Send();
        }
    }
    public bool IsHeld
    {
        get => _held; set
        {
            if (Set(ref _held, value))
                Send();
        }
    }
    public string RecordingName { get => _recordingName; set => Set(ref _recordingName, value); }
    public AsyncCommand SaveRecordingCommand { get; }
    public TouchNodeEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        _settings = (TouchNode)editor.Registry.Get(Type).Read(definition.Configuration);
        SaveRecordingCommand = new(() =>
        {
            var recording = Editor.Session.NodeStatus.GetValueOrDefault(Id)?.Recording;
            if (recording is not { Count: >= 2 })
                throw new LocalizedOperationException("Error.TouchRecording");
            var preset = new WaveformPreset(Guid.NewGuid(), string.IsNullOrWhiteSpace(RecordingName) ? L10n.Get("Touch.Title") : RecordingName,
                WaveShape.Sampled, recording.Count / 10d, 20, Samples: recording.ToArray());
            preset.Validate();
            Editor.AddRecording(preset);
            return Task.CompletedTask;
        }, editor.ReportError);
    }
    private void Send() => Editor.Session.Update(NodeControl.Create(Id, "touch", new TouchPoint(Frequency, PulseWidth / 100, IsHeld)));
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, _settings, Id);
}

public sealed class AudioNodeEditorViewModel : NodeEditorViewModel, IAsyncDisposable
{
    private AudioSettings _settings;
    private bool _calibrating;
    private CancellationTokenSource? _calibrationLifetime;
    private Task _calibrationTask = Task.CompletedTask;
    public IReadOnlyList<SessionOption> Sources { get; } = Enum.GetValues<AudioSourceKind>().Select(s => new SessionOption((int)s, "AudioSource." + s)).ToArray();
    public IReadOnlyList<SessionOption> FollowModes { get; } = Enum.GetValues<AudioFollowMode>().Select(s => new SessionOption((int)s, "AudioFollow." + s)).ToArray();
    public SessionOption Source
    {
        get => Sources.Single(s => s.Value == (int)_settings.Source); set
        {
            if (value is not null)
                ChangeSettings(_settings with
                {
                    Source = (AudioSourceKind)value.Value
                }, nameof(Source));
        }
    }
    public SessionOption Follow
    {
        get => FollowModes.Single(s => s.Value == (int)_settings.Follow); set
        {
            if (value is not null)
                ChangeSettings(_settings with
                {
                    Follow = (AudioFollowMode)value.Value
                }, nameof(Follow));
        }
    }
    public string MediaPath
    {
        get => _settings.MediaPath ?? ""; set => ChangeSettings(_settings with { MediaPath = value }, nameof(MediaPath));
    }
    public double NoiseFloorDb { get => _settings.NoiseFloorDb; set { if (double.IsFinite(value)) ChangeSettings(_settings with { NoiseFloorDb = Math.Clamp(value, -90, -30) }, nameof(NoiseFloorDb)); } }
    public double Sensitivity
    {
        get => _settings.Sensitivity; set
        {
            if (double.IsFinite(value))
                ChangeSettings(_settings with
                {
                    Sensitivity = Math.Clamp(value, 0, 100)
                }, nameof(Sensitivity));
        }
    }
    public double PlaybackVolume
    {
        get => _settings.PlaybackVolume; set
        {
            if (double.IsFinite(value))
                ChangeSettings(_settings with
                {
                    PlaybackVolume = Math.Clamp(value, 0, 100)
                }, nameof(PlaybackVolume));
        }
    }
    public bool LoopMedia
    {
        get => _settings.LoopMedia; set => ChangeSettings(_settings with { LoopMedia = value }, nameof(LoopMedia));
    }
    public bool IsMedia => _settings.Source == AudioSourceKind.MediaFile;
    public bool IsCalibrating => _calibrating;
    public string NoiseFloor => L10n.Format("Audio.NoiseFloorFormat", _settings.NoiseFloorDb);
    public AsyncCommand CalibrateAudioCommand { get; }
    public AudioNodeEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        _settings = ((AudioNode)editor.Registry.Get(Type).Read(definition.Configuration)).Settings;
        CalibrateAudioCommand = new(CalibrateAsync, editor.ReportError, () => CanEdit && !IsMedia && !_calibrating);
    }
    private void ChangeSettings(AudioSettings settings, string property)
    {
        if (_calibrating)
            return;
        settings.Validate();
        Change(() => { _settings = settings; Raise(property); Raise(nameof(IsMedia)); Raise(nameof(NoiseFloor)); });
    }
    private Task CalibrateAsync() => _calibrationTask = RunCalibrationAsync();
    private async Task RunCalibrationAsync()
    {
        _calibrating = true;
        Raise(nameof(IsCalibrating));
        Editor.Validate();
        IAudioInputSession? session = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _calibrationLifetime = cancellation;
        Exception? primary = null;
        double? calibratedFloor = null;
        try
        {
            session = await Editor.AudioInputs.StartAsync(_settings, cancellation.Token);
            List<double> levels = [];
            long sequence = -1;
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(100, cancellation.Token);
                if (session.Error is { } error)
                    throw error;
                if (session.Latest is { } frame && frame.Sequence != sequence && double.IsFinite(frame.LeftDb) && double.IsFinite(frame.RightDb))
                {
                    var age = DateTimeOffset.UtcNow - frame.CapturedAt;
                    if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(2) || frame.LeftDb is < -120 or > 0 || frame.RightDb is < -120 or > 0)
                        continue;
                    sequence = frame.Sequence;
                    levels.Add(Math.Clamp(Math.Max(frame.LeftDb, frame.RightDb), -120, 0));
                }
            }
            if (levels.Count == 0)
                throw new LocalizedOperationException("Error.AudioCalibrationNoData");
            levels.Sort();
            calibratedFloor = Math.Clamp(levels[(int)((levels.Count - 1) * .75)], -90, -30);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            cancellation.Cancel();
            try
            {
                if (session is not null)
                    await session.DisposeAsync();
            }
            catch (Exception cleanup) { primary = primary is null ? cleanup : new LocalizedAggregateException("Error.Cleanup", [primary, cleanup]); }
            _calibrating = false;
            _calibrationLifetime = null;
            Raise(nameof(IsCalibrating));
            Editor.Validate();
        }
        if (primary is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (calibratedFloor is { } floor)
            Change(() => { _settings = _settings with { NoiseFloorDb = floor }; Raise(nameof(NoiseFloor)); });
    }
    protected override NodeDefinition BuildDefinition() => NodeDefinition.Create(Type, new AudioNode(_settings), Id);
    public override void RefreshLocalization()
    {
        base.RefreshLocalization();
        foreach (var option in Sources.Concat(FollowModes))
            option.RefreshLocalization();
        Raise(nameof(NoiseFloor));
        CalibrateAudioCommand.NotifyCanExecuteChanged();
    }
    public async ValueTask DisposeAsync()
    {
        _calibrationLifetime?.Cancel();
        try
        {
            await _calibrationTask;
        }
        catch (OperationCanceledException) { }
    }
}
