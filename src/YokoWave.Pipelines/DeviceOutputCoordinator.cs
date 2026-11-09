using YokoWave.Core;

namespace YokoWave.Pipelines;

/// <summary>Combines each device's channels once, with channel-local caps applied before aggregation.</summary>
internal sealed class DeviceOutputCoordinator(PipelinePlan plan, IReadOnlySet<string> simulatedDevices)
{
    private readonly Dictionary<string, (double Peak, WaveformSample[] Samples)[]> _channels = [];
    private readonly Dictionary<string, PumpOutput> _pumps = [];
    private TimeSpan _elapsed;
    public void Clear(TimeSpan elapsed)
    {
        _elapsed = elapsed;
        _channels.Clear();
        _pumps.Clear();
    }

    private bool Accepts(string device, DateTimeOffset now, params NumericSignal[] signals) =>
        signals.All(s => s.IsValid(now) && (!s.Simulated || simulatedDevices.Contains(device)));

    public void Channel(ChannelOutputNode binding, NumericSignal strength, NumericSignal frequency,
        NumericSignal width, NumericSignal limit, DateTimeOffset now)
    {
        if (!_channels.TryGetValue(binding.DeviceId, out var channels))
            _channels[binding.DeviceId] = channels = new (double, WaveformSample[])[3];
        if (!Accepts(binding.DeviceId, now, strength, frequency, width, limit))
            return;
        var levels = Enumerable.Range(0, 4).Select(i => plan.Duration is { } duration && _elapsed.TotalSeconds + i * .025 >= duration.TotalSeconds
            ? 0 : Math.Min(strength.At(i), Math.Min(limit.At(i), plan.OutputLimit))).ToArray();
        var peak = levels.Max();
        var samples = Enumerable.Range(0, 4).Select(i => new WaveformSample(peak == 0 ? 0 : levels[i] / peak,
            frequency.At(i), width.At(i))).ToArray();
        channels[binding.Channel] = (peak * 100, samples);
    }

    public void Pump(PumpOutputNode binding, NumericSignal direction, NumericSignal water,
        NumericSignal peristalticSeconds, NumericSignal waterSeconds, DateTimeOffset now)
    {
        // Timed commands are discrete. A varying sample window is not a pump command.
        var signals = new[] { direction, water, peristalticSeconds, waterSeconds };
        if (!Accepts(binding.DeviceId, now, signals) || signals.Any(s => s.Samples.Distinct().Count() != 1) || plan.OutputLimit == 0)
        {
            _pumps[binding.DeviceId] = new();
            return;
        }
        _pumps[binding.DeviceId] = new((PumpDirection)(int)direction.At(0), water.At(0) == 1,
            (int)peristalticSeconds.At(0), (int)waterSeconds.At(0));
    }

    public IReadOnlyDictionary<string, OutputFrame> Build()
    {
        Dictionary<string, OutputFrame> frames = [];
        foreach (var id in plan.DeviceIds)
        {
            if (_pumps.TryGetValue(id, out var pump))
            {
                frames[id] = OutputFrame.Zero with { Pump = pump };
                continue;
            }
            if (!_channels.TryGetValue(id, out var channels))
            {
                frames[id] = OutputFrame.Zero;
                continue;
            }
            frames[id] = new(channels[0].Peak, channels[1].Peak, channels[0].Samples?[0].FrequencyHz ?? 20,
                channels[0].Samples?[0], channels[1].Samples?[0], ChannelC: channels[2].Peak,
                WindowA: channels[0].Samples, WindowB: channels[1].Samples,
                ReferenceSampleC: channels[2].Samples?[0], WindowC: channels[2].Samples);
        }
        return frames;
    }
}
