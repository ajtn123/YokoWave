using YokoWave.Core;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>Operation-specific behavior and timing, independent of queue ordering and network requests.</summary>
internal abstract class DgLabV4ScheduledTask(string id, DgLabV4Operation operation)
{
    private double? _started;
    public string Id { get; } = id;
    public DgLabV4Operation Operation { get; } = operation;
    public bool IsStarted => _started.HasValue;
    protected virtual double Lifetime => Operation.Duration == 0 ? double.PositiveInfinity : Operation.Duration;
    protected double Age(double now) => now - _started!.Value;
    protected bool IsActiveAt(double now) => _started.HasValue && Age(now) < Lifetime;

    public static DgLabV4ScheduledTask Create(string id, DgLabV4Operation operation)
    {
        operation.Validate();
        return operation.Type switch
        {
            0 => new PulseTask(id, operation, DgLabV4Pulse.Decode(operation.Value, operation.Version)),
            4 => new TemporaryIntensityTask(id, operation),
            _ => new IntensityCommandTask(id, operation)
        };
    }

    public bool Advance(double now, DgLabV4IntensityState intensity)
    {
        if (_started is null)
        {
            _started = now;
            Start(intensity);
        }
        return Age(now) >= Lifetime;
    }

    public virtual double TemporaryAt(double now) => 0;
    public virtual WaveformSample[]? WindowAt(double now) => null;
    protected virtual void Start(DgLabV4IntensityState intensity) { }

    private sealed class PulseTask(string id, DgLabV4Operation operation, WaveformSample[][] frames) : DgLabV4ScheduledTask(id, operation)
    {
        protected override double Lifetime => Math.Min(base.Lifetime, frames.Length * 100);
        public override WaveformSample[] WindowAt(double now)
        {
            int frame = Math.Clamp((int)(Age(now) / 100), 0, frames.Length - 1);
            return Enumerable.Range(0, 4).Select(index => IsActiveAt(now + index * 25)
                ? frames[frame][index] : new WaveformSample(0, 1, 0)).ToArray();
        }
    }

    private sealed class TemporaryIntensityTask(string id, DgLabV4Operation operation) : DgLabV4ScheduledTask(id, operation)
    {
        public override double TemporaryAt(double now) => IsActiveAt(now) ? Operation.Value.GetDouble() : 0;
    }

    private sealed class IntensityCommandTask(string id, DgLabV4Operation operation) : DgLabV4ScheduledTask(id, operation)
    {
        protected override double Lifetime => 0;
        protected override void Start(DgLabV4IntensityState intensity)
        {
            if (Operation.Type == 7)
                intensity.Reset();
            else
                intensity.Adjust(Operation.Value.GetDouble());
        }
    }
}

internal sealed class DgLabV4IntensityState(double maximum)
{
    public double Value { get; private set; }
    public void Adjust(double delta) => Value = Math.Clamp(Value + delta, 0, maximum);
    public void Reset() => Value = 0;
}
