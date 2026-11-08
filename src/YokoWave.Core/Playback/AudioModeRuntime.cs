namespace YokoWave.Core;

internal sealed class AudioModeRuntime(AudioSettings settings, TimeProvider clock) : PlaybackModeRuntime, IAudioPlaybackInput
{
    private readonly AudioEnvelope _left = new(), _right = new();
    private readonly Dictionary<string, PlaybackInputStatus> _status = [];
    private AudioFeatureFrame? _audio;
    private long _sequence = -1;
    private double _leftScale, _rightScale;
    public override IReadOnlyDictionary<string, PlaybackInputStatus> Status => _status;
    public void SetAudio(AudioFeatureFrame? frame) => _audio = frame;
    public override void Suspend()
    {
        _left.Reset();
        _right.Reset();
        _audio = null;
        _sequence = -1;
        _leftScale = _rightScale = 0;
    }
    public override OutputFrame Render(PlaybackContext context, OutputFrame frame)
    {
        var audio = _audio;
        if (audio is null || !PlaybackInput.IsFresh(audio.CapturedAt, clock) ||
            new[] { audio.LeftDb, audio.RightDb, audio.LeftOnset, audio.RightOnset }.Any(value => !double.IsFinite(value)) ||
            audio.LeftDb is < -120 or > 0 || audio.RightDb is < -120 or > 0 || audio.LeftOnset is < 0 or > 1 || audio.RightOnset is < 0 or > 1)
        {
            _left.Reset();
            _right.Reset();
            _leftScale = _rightScale = 0;
            _status[context.DeviceId] = new(context.DeviceId, InputPhase.Waiting);
            return frame with
            {
                ChannelA = 0,
                ChannelB = 0,
                ChannelC = 0
            };
        }
        // One envelope update per PCM frame, regardless of the number of target devices.
        if (audio.Sequence != _sequence)
        {
            _sequence = audio.Sequence;
            _leftScale = _left.Process(audio.LeftDb, audio.LeftOnset, settings);
            _rightScale = _right.Process(audio.RightDb, audio.RightOnset, settings);
        }
        var level = Math.Max(_leftScale, _rightScale);
        _status[context.DeviceId] = new(context.DeviceId, level > 0 ? InputPhase.Tracking : InputPhase.Safe, Level: level);
        return ParameterPlayback.Scale(frame, _leftScale, _rightScale);
    }
}
