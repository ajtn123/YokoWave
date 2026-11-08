using YokoWave.Core.Localization;

namespace YokoWave.Core;

/// <summary>Two independently timed tracks, shared by waveform and activity modes.</summary>
internal sealed class WaveformPlayback
{
    private readonly WaveformTrack _a;
    private WaveformTrack? _b;
    private readonly IReadOnlyList<WaveformPreset> _playlist;
    private readonly PlaybackOrder _order;
    private readonly Random _random;
    public WaveformPreset Current => _a.Current;
    public WaveformPreset CurrentB => _b?.Current ?? Current;
    public WaveformPreset? SeparateB => _b?.Current;
    public TimeSpan Elapsed => _a.Elapsed;

    public WaveformPlayback(SessionProgram program, WaveformPreset preset, Random random, PlaybackOrder order = PlaybackOrder.Repeat)
    {
        _playlist = program.Playlist?.ToArray() ?? [preset];
        _order = order;
        if (_order is PlaybackOrder.Sequential or PlaybackOrder.Random && program.Playlist is not { Count: > 0 })
            throw new LocalizedOperationException("Error.EmptyPlaylist");
        _random = random;
        _a = new(preset, _playlist);
        _b = program.ChannelBWaveform is { } separate ? new(separate, _playlist) : null;
    }
    public void Change(WaveformPreset preset, bool channelB)
    {
        if (_order is PlaybackOrder.Sequential or PlaybackOrder.Random && !_playlist.Any(wave => wave.Id == preset.Id))
            throw new LocalizedOperationException("Error.PlaylistWave");
        if (channelB) { _b ??= new(preset, _playlist); _b.Change(preset); }
        else _a.Change(preset);
    }
    public bool Advance(TimeSpan elapsed)
    {
        _a.Advance(elapsed, _order, _random); _b?.Advance(elapsed, _order, _random);
        return _a.Completed && (_b?.Completed ?? true);
    }
    public OutputFrame Render(PlaybackContext context, OutputFrame frame)
    {
        var timeA = _a.Elapsed.TotalSeconds; var timeB = _b?.Elapsed.TotalSeconds ?? timeA;
        var phaseB = _b is null && context.OffsetChannelB ? .5 : 0;
        return frame with
        {
            ChannelA = _a.Completed ? 0 : frame.ChannelA,
            ChannelB = (_b?.Completed ?? _a.Completed) ? 0 : frame.ChannelB,
            ChannelC = _a.Completed ? 0 : frame.ChannelC,
            Frequency = Current.Frequency,
            ReferenceSample = WaveformGenerator.At(Current, timeA),
            ReferenceSampleB = WaveformGenerator.At(CurrentB, timeB, phaseB),
            WindowA = WaveformGenerator.Window(Current, timeA, repeat: _order != PlaybackOrder.Once, remainingSeconds: context.RemainingSeconds),
            WindowB = WaveformGenerator.Window(CurrentB, timeB, phaseB, _order != PlaybackOrder.Once, context.RemainingSeconds)
        };
    }
}
