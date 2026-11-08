namespace YokoWave.Core;

internal sealed class TouchModeRuntime(TouchPlayback playback) : PlaybackModeRuntime, ITouchPlaybackInput
{
    public const int MaxRecordingFrames = 256;
    private WaveformSample _sample = new(1, 20, .5);
    private readonly List<WaveformSample> _recording = [];
    private long _recordBucket = -1;
    private double _replayEpoch;
    private bool _held, _hasTouch;
    public IReadOnlyList<WaveformSample> Recording => _recording.ToArray();

    public void SetTouch(double frequency, double pulseWidth, bool held, double elapsed)
    {
        _sample = new(1, frequency, pulseWidth / 100);
        if (held && !_held) { _recording.Clear(); _recordBucket = -1; }
        if (!held && _held) _replayEpoch = elapsed;
        _held = held; _hasTouch |= held;
    }
    public override void Suspend() => _held = false;
    public override string? Advance(TimeSpan elapsed)
    {
        if (_held && _recording.Count < MaxRecordingFrames)
        {
            var bucket = (long)Math.Floor(elapsed.TotalSeconds * 10);
            if (bucket != _recordBucket) { _recording.Add(_sample); _recordBucket = bucket; }
        }
        return null;
    }
    public override OutputFrame Render(PlaybackContext context, OutputFrame frame)
    {
        var touch = _held || _hasTouch && playback == TouchPlayback.HoldLast ? _sample
            : playback == TouchPlayback.RecordedLoop && _recording.Count > 0
                ? _recording[(int)Math.Floor((context.Elapsed.TotalSeconds - _replayEpoch) * 10) % _recording.Count] : null;
        return frame with { ReferenceSample = touch, ReferenceSampleB = touch,
            ChannelA = touch is null ? 0 : frame.ChannelA, ChannelB = touch is null ? 0 : frame.ChannelB,
            ChannelC = touch is null ? 0 : frame.ChannelC };
    }
}
