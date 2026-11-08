using YokoWave.Core.Localization;

namespace YokoWave.Core;

public enum PlaybackOrder { Repeat, Once, Sequential, Random }
public enum StrengthMode { Fixed, RandomIncrease, StepIncrease }
public enum TouchPlayback { Momentary, RecordedLoop, HoldLast }

public sealed record StrengthSettings(StrengthMode Mode = StrengthMode.Fixed, double IntervalSeconds = 10,
    double RandomAddition = 10, double Step = 1, double Ceiling = 60, double Boost = 10)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || !double.IsFinite(IntervalSeconds) || IntervalSeconds is < 1 or > 600 ||
            new[] { RandomAddition, Step, Ceiling, Boost }.Any(value => !double.IsFinite(value) || value is < 0 or > 100))
            throw new LocalizedArgumentException("Error.StrengthSettings");
    }
}

public sealed record EstimSettings(PlaybackOrder Order = PlaybackOrder.Repeat, StrengthSettings? Strength = null,
    bool SeparateWaveforms = false, bool Gentle = false, bool ShowContacts = true, TouchPlayback TouchPlayback = TouchPlayback.Momentary)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Order) || !Enum.IsDefined(TouchPlayback)) throw new LocalizedArgumentException("Error.PlaybackSettings");
        (Strength ?? new()).Validate();
    }
}

public sealed record SessionProgram(EstimSettings? Settings = null, IReadOnlyList<WaveformPreset>? Playlist = null,
    WaveformPreset? ChannelBWaveform = null, bool TouchControl = false);

internal sealed class WaveformTrack(WaveformPreset preset, IReadOnlyList<WaveformPreset> playlist)
{
    public WaveformPreset Current { get; private set; } = preset;
    public TimeSpan Elapsed { get; private set; }
    public bool Completed { get; private set; }
    private TimeSpan _started;
    private bool _restart;

    public void Change(WaveformPreset next) { Current = next; Elapsed = TimeSpan.Zero; Completed = false; _restart = true; }

    public void Advance(TimeSpan elapsed, PlaybackOrder order, Random random)
    {
        if (_restart) { _started = elapsed; _restart = false; }
        Elapsed = elapsed - _started;
        if (Elapsed.TotalSeconds < Current.PeriodSeconds || order == PlaybackOrder.Repeat) return;
        if (order == PlaybackOrder.Once) { Completed = true; return; }
        var candidates = playlist.Where(wave => (wave.Family == WaveformFamily.Motor) == (Current.Family == WaveformFamily.Motor)).ToArray();
        if (candidates.Length == 0) { _started = elapsed; Elapsed = TimeSpan.Zero; return; }
        var index = Array.FindIndex(candidates, wave => wave.Id == Current.Id);
        if (order == PlaybackOrder.Sequential) index = (index + 1) % candidates.Length;
        else if (candidates.Length > 1)
        {
            var choices = candidates.Where(wave => wave.Id != Current.Id).ToArray();
            Current = choices[random.Next(choices.Length)];
            _started = elapsed; Elapsed = TimeSpan.Zero; return;
        }
        else index = 0;
        Current = candidates[index]; _started = elapsed; Elapsed = TimeSpan.Zero;
    }
}
