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
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) throw new InvalidOperationException("请先停止当前会话。");
            var ids = targets.Distinct().ToArray();
            if (ids.Length == 0 || ids.Any(id => !controller.Connections.ContainsKey(id)))
                throw new InvalidOperationException("请先连接并选择至少一个设备。");
            if (duration.TotalSeconds is < 1 or > 7200 || !double.IsFinite(duration.TotalSeconds))
                throw new ArgumentException("会话时长应在 1 秒至 120 分钟之间。");
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
            if (elapsed < Elapsed) throw new ArgumentException("会话时间不能倒退。");
            Elapsed = elapsed;
            if (elapsed.TotalSeconds >= _duration) { Elapsed = TimeSpan.FromSeconds(_duration); await StopCoreAsync("定时结束"); return; }
            try
            {
                foreach (var id in _targets)
                {
                    await controller.SendAsync(id, new(
                        ChannelA * WaveformGenerator.Sample(_preset, elapsed.TotalSeconds),
                        ChannelB * WaveformGenerator.Sample(_preset, elapsed.TotalSeconds, OffsetChannelB ? 0.5 : 0),
                        _preset.Frequency));
                    var output = controller.Connections[id].Output;
                    _peakA = Math.Max(_peakA, output.ChannelA);
                    _peakB = Math.Max(_peakB, output.ChannelB);
                }
            }
            catch { await StopCoreAsync("连接异常"); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(string reason = "手动停止")
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
                    _preset.Name, _targets.Length, _peakA, _peakB, reason));
        }
    }
}
