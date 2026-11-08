namespace YokoWave.Core;

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
        var candidates = playlist.ToArray();
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
