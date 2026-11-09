using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.App.ViewModels.Pipelines;

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
        get => Sources.Single(s => s.Value == (int)_settings.Source);
        set
        {
            if (value is not null)
            {
                ChangeSettings(_settings with
                {
                    Source = (AudioSourceKind)value.Value
                }, nameof(Source));
            }
        }
    }
    public SessionOption Follow
    {
        get => FollowModes.Single(s => s.Value == (int)_settings.Follow);
        set
        {
            if (value is not null)
            {
                ChangeSettings(_settings with
                {
                    Follow = (AudioFollowMode)value.Value
                }, nameof(Follow));
            }
        }
    }
    public string MediaPath
    { get => _settings.MediaPath ?? ""; set => ChangeSettings(_settings with { MediaPath = value }, nameof(MediaPath)); }
    public double NoiseFloorDb
    {
        get => _settings.NoiseFloorDb;
        set { if (double.IsFinite(value)) ChangeSettings(_settings with { NoiseFloorDb = Math.Clamp(value, -90, -30) }, nameof(NoiseFloorDb)); }
    }
    public double Sensitivity
    {
        get => _settings.Sensitivity;
        set
        {
            if (double.IsFinite(value))
            {
                ChangeSettings(_settings with
                {
                    Sensitivity = Math.Clamp(value, 0, 100)
                }, nameof(Sensitivity));
            }
        }
    }
    public double PlaybackVolume
    {
        get => _settings.PlaybackVolume;
        set
        {
            if (double.IsFinite(value))
            {
                ChangeSettings(_settings with
                {
                    PlaybackVolume = Math.Clamp(value, 0, 100)
                }, nameof(PlaybackVolume));
            }
        }
    }
    public bool LoopMedia
    { get => _settings.LoopMedia; set => ChangeSettings(_settings with { LoopMedia = value }, nameof(LoopMedia)); }
    public bool IsMedia => _settings.Source == AudioSourceKind.MediaFile;
    public bool IsCalibrating => _calibrating;
    public string NoiseFloor => L10n.Format("Audio.NoiseFloorFormat", _settings.NoiseFloorDb);
    public AsyncCommand CalibrateAudioCommand { get; }
    public AudioNodeEditorViewModel(PipelineEditorViewModel editor, NodeDefinition definition) : base(editor, definition)
    {
        _settings = ((AudioNode)editor.Registry.Get(Type).Read(definition.Configuration)).Settings;
        CalibrateAudioCommand = new(CalibrateAsync, editor.ReportError, () => CanEdit && !IsMedia && !_calibrating);
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
    private void ChangeSettings(AudioSettings settings, string property)
    {
        if (_calibrating)
            return;
        settings.Validate();
        Change(() =>
        {
            _settings = settings;
            Raise(property);
            Raise(nameof(IsMedia));
            Raise(nameof(NoiseFloor));
        });
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
            for (int i = 0; i < 20; i++)
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
        catch (Exception error)
        {
            primary = error;
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                if (session is not null)
                    await session.DisposeAsync();
            }
            catch (Exception cleanup)
            {
                primary = primary is null ? cleanup : new LocalizedAggregateException("Error.Cleanup", [primary, cleanup]);
            }
            _calibrating = false;
            _calibrationLifetime = null;
            Raise(nameof(IsCalibrating));
            Editor.Validate();
        }
        if (primary is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (calibratedFloor is { } floor)
        {
            Change(() =>
            {
                _settings = _settings with { NoiseFloorDb = floor };
                Raise(nameof(NoiseFloor));
            });
        }
    }
}
