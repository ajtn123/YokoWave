using YokoWave.Core;

namespace YokoWave.Remote.DgLab.V4;

public sealed record DgLabV4TaskResult(string Id, int Type, int Channel, string Reason);

/// <summary>Synchronous task evaluation. The graph owns its clock, limits and device output.</summary>
public sealed class DgLabV4Scheduler
{
    private readonly double _maximum;
    private readonly List<ScheduledTask> _tasks = [];
    private readonly Queue<DgLabV4TaskResult> _completed = [];
    private readonly double[] _baseIntensity = [0, 0];
    public int Count => _tasks.Count;

    public DgLabV4Scheduler(double maximum = 200)
    {
        if (!double.IsFinite(maximum) || maximum <= 0)
            throw new DgLabV4Exception("invalid_params");
        _maximum = maximum;
    }

    public void Submit(string id, DgLabV4Operation operation)
    {
        operation.Validate();
        if (_tasks.Any(task => task.Id == id))
            throw new DgLabV4Exception("duplicate_request_id");
        if (_tasks.Count >= 256)
            throw new DgLabV4Exception("invalid_operate");
        if (operation.Immediate)
        {
            foreach (var previous in _tasks.Where(task => task.Operation.Channel == operation.Channel && task.Operation.Type == operation.Type).ToArray())
                Finish(previous, "replaced");
        }
        _tasks.Add(new(id, operation, operation.Type == 0 ? DgLabV4Pulse.Decode(operation.Value, operation.Version) : []));
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
        Array.Clear(_baseIntensity);
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
                task.Started ??= now;
                var operation = task.Operation;
                if (operation.Type is 3 or 7)
                {
                    int channel = operation.Channel;
                    _baseIntensity[channel] = operation.Type == 7 ? 0 : Math.Clamp(_baseIntensity[channel] + operation.Value.GetDouble(), 0, _maximum);
                    Finish(task, "completed");
                    continue;
                }
                double age = now - task.Started.Value;
                if (operation.Duration > 0 && age >= operation.Duration || operation.Type == 0 && age >= task.Frames.Length * 100)
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
            var active = _tasks.Where(task => task.Operation.Channel == channel && task.Started.HasValue).ToArray();
            double temporary = active.Where(task => task.Operation.Type == 4).Sum(task => task.Operation.Value.GetDouble());
            levels[channel] = Math.Clamp(_baseIntensity[channel] + temporary, 0, _maximum) * 100 / _maximum;
            var pulse = active.Where(task => task.Operation.Type == 0).OrderByDescending(task => task.Operation.Priority).FirstOrDefault();
            samples[channel] = pulse is null ? Enumerable.Repeat(new WaveformSample(1, 20, 0), 4).ToArray()
                : pulse.Frames[Math.Min(pulse.Frames.Length - 1, (int)((now - pulse.Started!.Value) / 100))];
        }
        return new(levels[0], levels[1], samples[0][0].FrequencyHz, ReferenceSample: samples[0][0], ReferenceSampleB: samples[1][0],
            WindowA: samples[0], WindowB: samples[1]);
    }

    private void Finish(ScheduledTask task, string reason)
    {
        _tasks.Remove(task);
        _completed.Enqueue(new(task.Id, task.Operation.Type, task.Operation.Channel, reason));
    }

    private sealed class ScheduledTask(string id, DgLabV4Operation operation, WaveformSample[][] frames)
    {
        public string Id { get; } = id;
        public DgLabV4Operation Operation { get; } = operation;
        public WaveformSample[][] Frames { get; } = frames;
        public double? Started { get; set; }
    }
}
