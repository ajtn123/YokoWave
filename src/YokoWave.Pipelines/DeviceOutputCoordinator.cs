using YokoWave.Core;

namespace YokoWave.Pipelines;

/// <summary>Combines each device's channels once, with channel-local caps applied before aggregation.</summary>
internal sealed class DeviceOutputCoordinator(PipelinePlan plan, IReadOnlySet<string> simulatedDevices)
{
    private readonly Dictionary<Guid, double> _limits = [];
    public void SetLimit(Guid node, double value) => _limits[node] = value;
    public double Limit(Guid node, double configured) => _limits.GetValueOrDefault(node, configured);
    private readonly Dictionary<string, (double Peak, WaveformSample[] Samples)[]> _channels = [];
    private readonly Dictionary<string, PumpOutput> _pumps = [];
    private readonly Dictionary<string, OutputFrame> _native = [];
    private readonly Dictionary<string, OutputFrame> _options = [];
    private readonly HashSet<string> _validChannels = new(StringComparer.Ordinal);
    public void Zero(string deviceId) => _native[deviceId] = OutputFrame.Zero;
    private TimeSpan _elapsed;
    public void Clear(TimeSpan elapsed)
    {
        _elapsed = elapsed;
        _channels.Clear();
        _pumps.Clear();
        _native.Clear();
        _options.Clear();
        _validChannels.Clear();
    }

    private bool Accepts(string device, DateTimeOffset now, params NumericSignal[] signals) =>
        signals.All(s => s.IsValid(now) && (!s.Simulated || simulatedDevices.Contains(device)));

    public void Channel(ChannelOutputNode binding, NumericSignal strength, NumericSignal frequency,
        NumericSignal width, NumericSignal limit, DateTimeOffset now, OutputFrame? options = null)
    {
        if (!_channels.TryGetValue(binding.DeviceId, out var channels))
            _channels[binding.DeviceId] = channels = new (double, WaveformSample[])[3];
        if (!Accepts(binding.DeviceId, now, strength, frequency, width, limit))
            return;
        _validChannels.Add(binding.DeviceId);
        if (options is not null)
            _options[binding.DeviceId] = options;
        var levels = Enumerable.Range(0, 4).Select(i => plan.Duration is { } duration && _elapsed.TotalSeconds + i * .025 >= duration.TotalSeconds
            ? 0 : Math.Min(strength.At(i), Math.Min(limit.At(i), binding.Limit))).ToArray();
        var peak = levels.Max();
        var samples = Enumerable.Range(0, 4).Select(i => new WaveformSample(peak == 0 ? 0 : levels[i] / peak,
            frequency.At(i), width.At(i))).ToArray();
        channels[binding.Channel] = (peak * 100, samples);
    }

    public void Native(NativeOutputNode binding, double a, double b, double c, NativeProgramSettings program, TimeSpan elapsed, double? remaining = null, OutputFrame? options = null)
    {
        var presets = program.Kind is NativeProgramKind.ElectricalPreset or NativeProgramKind.MotorPreset;
        var frame = new OutputFrame(presets && program.PresetA == 0 ? 0 : Math.Min(a, binding.Limit) * 100,
            presets && program.PresetB == 0 ? 0 : Math.Min(b, binding.Limit) * 100, 20,
            ChannelC: program.Kind == NativeProgramKind.MotorPreset && program.PresetC > 0 ? Math.Min(c, binding.Limit) * 100 : 0,
            OutputLimit: binding.Limit * 100, NativeProgram: new(program, elapsed.TotalSeconds,
                plan.Duration is { } duration ? Math.Min(remaining ?? double.MaxValue, Math.Max(0, (duration - _elapsed).TotalSeconds)) : remaining),
            Hardware: options?.Hardware, DgBalances: options?.DgBalances, Gentle: options?.Gentle ?? false);
        frame.NativeProgram!.ValidateOutput(frame);
        _native[binding.DeviceId] = frame;
    }

    public void Pump(PumpOutputNode binding, NumericSignal direction, NumericSignal water,
        NumericSignal peristalticSeconds, NumericSignal waterSeconds, DateTimeOffset now)
    {
        // Timed commands are discrete. A varying sample window is not a pump command.
        var signals = new[] { direction, water, peristalticSeconds, waterSeconds };
        if (!Accepts(binding.DeviceId, now, signals) || signals.Any(s => s.Samples.Distinct().Count() != 1) || binding.Limit == 0)
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
        foreach (var id in plan.OutputDeviceIds)
        {
            if (_native.TryGetValue(id, out var native))
            {
                frames[id] = native;
                continue;
            }
            if (_pumps.TryGetValue(id, out var pump))
            {
                frames[id] = OutputFrame.Zero with
                {
                    Pump = pump
                };
                continue;
            }
            if (!_channels.TryGetValue(id, out var channels) || !_validChannels.Contains(id))
            {
                frames[id] = OutputFrame.Zero;
                continue;
            }
            var options = _options.GetValueOrDefault(id);
            frames[id] = new(channels[0].Peak, channels[1].Peak, channels[0].Samples?[0].FrequencyHz ?? 20,
                channels[0].Samples?[0], channels[1].Samples?[0], ChannelC: channels[2].Peak,
                WindowA: channels[0].Samples, WindowB: channels[1].Samples,
                ReferenceSampleC: channels[2].Samples?[0], WindowC: channels[2].Samples,
                Hardware: options?.Hardware, DgBalances: options?.DgBalances, Gentle: options?.Gentle ?? false);
        }
        return frames;
    }
}
