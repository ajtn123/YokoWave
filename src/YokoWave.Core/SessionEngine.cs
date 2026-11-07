using YokoWave.Core.Localization;
namespace YokoWave.Core;

/// <summary>Caller supplies monotonic elapsed time. All lifecycle changes serialize with output ticks.</summary>
public sealed class SessionEngine(DeviceController controller)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string[] _targets = [];
    private WaveformPreset? _preset;
    private DateTimeOffset _startedAt;
    private double _duration;
    private double _peakA;
    private double _peakB;
    public bool IsRunning { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    public double ChannelA { get; set; }
    public double ChannelB { get; set; }
    public bool OffsetChannelB { get; set; } = true;
    public event EventHandler<SessionRecord>? Completed;

    public async Task StartAsync(IEnumerable<string> targets, WaveformPreset preset, TimeSpan duration)
    {
        preset.Validate();
        if (!controller.IsSimulation && preset.Family == WaveformFamily.Motor)
            throw new LocalizedArgumentException("Error.ElectricalWaveform");
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) throw new LocalizedOperationException("Error.AlreadyRunning");
            var ids = targets.Distinct().ToArray();
            if (ids.Length == 0 || ids.Any(id => !controller.Connections.ContainsKey(id)))
                throw new LocalizedOperationException("Error.SelectConnected");
            if (duration.TotalSeconds is < 1 or > 7200 || !double.IsFinite(duration.TotalSeconds))
                throw new LocalizedArgumentException("Error.Duration");
            await controller.StopAllAsync();
            _targets = ids;
            _preset = preset;
            _duration = duration.TotalSeconds;
            Elapsed = TimeSpan.Zero;
            _startedAt = DateTimeOffset.Now;
            _peakA = _peakB = 0;
            IsRunning = true;
        }
        finally { _gate.Release(); }
    }

    public async Task AdvanceAsync(TimeSpan elapsed)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning || _preset is null) return;
            if (elapsed < Elapsed) throw new LocalizedArgumentException("Error.Clock");
            Elapsed = elapsed;
            if (elapsed.TotalSeconds >= _duration) { Elapsed = TimeSpan.FromSeconds(_duration); await StopCoreAsync("Reason.Timer"); return; }
            try
            {
                foreach (var id in _targets)
                {
                    await controller.SendAsync(id, new(
                        _preset.Family == WaveformFamily.Electrical ? ChannelA : ChannelA * WaveformGenerator.Sample(_preset, elapsed.TotalSeconds),
                        _preset.Family == WaveformFamily.Electrical ? ChannelB : ChannelB * WaveformGenerator.Sample(_preset, elapsed.TotalSeconds, OffsetChannelB ? 0.5 : 0),
                        _preset.Frequency, WaveformGenerator.ReferenceSample(_preset, elapsed.TotalSeconds),
                        WaveformGenerator.ReferenceSample(_preset, elapsed.TotalSeconds, OffsetChannelB ? 0.5 : 0)));
                    var output = controller.Connections[id].Output;
                    _peakA = Math.Max(_peakA, output.ChannelA);
                    _peakB = Math.Max(_peakB, output.ChannelB);
                }
            }
            catch { await StopCoreAsync("Reason.ConnectionError"); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(string reason = "Reason.Manual")
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(reason); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(string reason)
    {
        var wasRunning = IsRunning;
        IsRunning = false;
        try { await controller.StopAllAsync(); }
        finally
        {
            if (wasRunning && _preset is not null)
                Completed?.Invoke(this, new(Guid.NewGuid(), _startedAt, Elapsed.TotalSeconds,
                    _preset.Name, _targets.Length, _peakA, _peakB, reason, _preset.Id));
        }
    }
}
