using YokoWave.Core;

namespace YokoWave.Remote.DgLab.V4;

public sealed record DgLabV4TaskResult(string Id, int Type, int Channel, string Reason);

/// <summary>Synchronous task evaluation. The graph owns its clock, limits and device output.</summary>
public sealed class DgLabV4Scheduler
{
    private readonly double _maximum;
    private readonly List<DgLabV4ScheduledTask> _tasks = [];
    private readonly Queue<DgLabV4TaskResult> _completed = [];
    private readonly DgLabV4IntensityState[] _intensities;
    public int Count => _tasks.Count;

    public DgLabV4Scheduler(double maximum = 200)
    {
        if (!double.IsFinite(maximum) || maximum <= 0)
            throw new DgLabV4Exception("invalid_params");
        _maximum = maximum;
        _intensities = [new(maximum), new(maximum)];
    }

    public void Submit(string id, DgLabV4Operation operation)
    {
        var pending = DgLabV4ScheduledTask.Create(id, operation);
        if (_tasks.Any(task => task.Id == id))
            throw new DgLabV4Exception("duplicate_request_id");
        if (_tasks.Count >= 256 && !operation.Immediate)
            throw new DgLabV4Exception("invalid_operate");
        if (operation.Immediate)
        {
            foreach (var previous in _tasks.Where(task => task.Operation.Channel == operation.Channel && task.Operation.Type == operation.Type).ToArray())
                Finish(previous, "replaced");
        }
        if (_tasks.Count >= 256)
            throw new DgLabV4Exception("invalid_operate");
        _tasks.Add(pending);
    }

    public void Clear(int? channel = null, string reason = "cleared")
    {
        if (channel is < 0 or > 1)
            throw new DgLabV4Exception("invalid_params");
        foreach (var task in _tasks.Where(task => channel is null || task.Operation.Channel == channel).ToArray())
            Finish(task, reason);
    }

    public void Cancel()
    {
        Clear(reason: "cancelled");
        foreach (var intensity in _intensities)
            intensity.Reset();
    }

    public IReadOnlyList<DgLabV4TaskResult> DrainCompleted()
    {
        var results = _completed.ToArray();
        _completed.Clear();
        return results;
    }

    public OutputFrame Evaluate(TimeSpan elapsed)
    {
        double now = elapsed.TotalMilliseconds;
        foreach (var group in _tasks.GroupBy(task => (task.Operation.Channel, task.Operation.Type, task.Operation.Priority)).ToArray())
        {
            foreach (var task in group.ToArray())
            {
                if (task.Advance(now, _intensities[task.Operation.Channel]))
                {
                    Finish(task, "completed");
                    continue;
                }
                break;
            }
        }
        var samples = new WaveformSample[2][];
        double[] levels = [0, 0];
        for (int channel = 0; channel < 2; channel++)
        {
            var active = _tasks.Where(task => task.Operation.Channel == channel && task.IsStarted).ToArray();
            double[] strengths = Enumerable.Range(0, 4).Select(index => Math.Clamp(_intensities[channel].Value +
                active.Sum(task => task.TemporaryAt(now + index * 25)), 0, _maximum)).ToArray();
            double peak = strengths.Max();
            levels[channel] = peak * 100 / _maximum;
            var pulse = active.OrderByDescending(task => task.Operation.Priority).Select(task => task.WindowAt(now)).FirstOrDefault(window => window is not null)
                ?? Enumerable.Repeat(new WaveformSample(1, 20, 0), 4).ToArray();
            samples[channel] = pulse.Select((sample, index) => sample with { Level = sample.Level * (peak > 0 ? strengths[index] / peak : 0) }).ToArray();
        }
        return new(levels[0], levels[1], samples[0][0].FrequencyHz, ReferenceSample: samples[0][0], ReferenceSampleB: samples[1][0],
            WindowA: samples[0], WindowB: samples[1]);
    }

    private void Finish(DgLabV4ScheduledTask task, string reason)
    {
        _tasks.Remove(task);
        _completed.Enqueue(new(task.Id, task.Operation.Type, task.Operation.Channel, reason));
    }

}
