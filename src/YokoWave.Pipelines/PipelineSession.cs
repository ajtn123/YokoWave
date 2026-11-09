using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Pipelines;

public enum PipelineState { Idle, Starting, Running, Paused, Stopping, Faulted }

/// <summary>Serial background clock and device lifecycle for a compiled graph.</summary>
public sealed class PipelineSession(DeviceController controller) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;
    private PipelineRuntime? _runtime;
    private PipelinePlan? _plan;
    private long _lastTimestamp;
    private TimeSpan _elapsed;
    private readonly Dictionary<string, PumpOutput?> _sentPumps = [];
    private bool _disposed;
    public Guid OwnerId { get; } = Guid.NewGuid();
    public PipelineState State { get; private set; }
    public bool HoldsResources => State != PipelineState.Idle;
    public TimeSpan Elapsed => _elapsed;
    public TimeSpan? Remaining => _plan?.Duration is { } duration ? TimeSpan.FromTicks(Math.Max(0, (duration - _elapsed).Ticks)) : null;
    public Exception? Error { get; private set; }
    public PipelineFrame? LastFrame { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler<SessionRecord>? Completed;
    private DateTimeOffset _startedAt;
    private double _peakA, _peakB, _peakC;
    private bool _recorded;

    public async Task StartAsync(PipelinePlan plan)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HoldsResources)
                throw new LocalizedOperationException("Error.AlreadyRunning");
            State = PipelineState.Starting;
            Error = null;
            _plan = plan;
            _elapsed = TimeSpan.Zero;
            _sentPumps.Clear();
            _recorded = false;
            _startedAt = default;
            _peakA = _peakB = _peakC = 0;
            foreach (var binding in plan.Bindings)
                if (!controller.Connections.TryGetValue(binding.DeviceId, out var connection) || !PipelineCompiler.Accepts(binding, connection.Device))
                    throw new LocalizedOperationException("Pipeline.Error.Device");
            _runtime = new(plan, plan.DeviceIds.Where(id => controller.AreSimulated([id])).ToHashSet(StringComparer.Ordinal));
            await controller.AcquireTargetsAsync(OwnerId, plan.DeviceIds);
            _startedAt = controller.Clock.GetUtcNow();
            _lastTimestamp = controller.Clock.GetTimestamp();
            State = PipelineState.Running;
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _loop = Task.Run(() => RunAsync(cancellation));
        }
        catch (Exception error)
        {
            Error = error;
            State = plan.DeviceIds.Any(id => controller.DeviceOwner(id) == OwnerId) ? PipelineState.Faulted : PipelineState.Idle;
            throw;
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    private async Task RunAsync(CancellationTokenSource cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), controller.Clock);
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    if (_cancellation != cancellation || State is not (PipelineState.Running or PipelineState.Paused))
                        return;
                    var now = controller.Clock.GetTimestamp();
                    if (State == PipelineState.Running)
                        _elapsed += controller.Clock.GetElapsedTime(_lastTimestamp, now);
                    _lastTimestamp = now;
                    if (Remaining == TimeSpan.Zero)
                    {
                        await StopCoreAsync("Reason.Timer").ConfigureAwait(false);
                        return;
                    }
                    if (State == PipelineState.Running)
                    {
                        LastFrame = _runtime!.Evaluate(_elapsed, controller.Clock.GetUtcNow());
                        await SubmitAsync(LastFrame).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    Error = error;
                    try
                    {
                        await StopCoreAsync("Reason.PipelineError").ConfigureAwait(false);
                    }
                    catch (Exception cleanupError)
                    {
                        Error = new LocalizedAggregateException("Error.StopUnconfirmed", [error, cleanupError]);
                    }
                    return;
                }
                finally
                {
                    _gate.Release();
                    Notify();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            cancellation.Dispose();
        }
    }

    public bool SetConstant(Guid node, double value) => _runtime?.SetConstant(node, value) == true;

    private async Task SubmitAsync(PipelineFrame frame)
    {
        var pending = frame.Outputs.Where(pair => pair.Value.Pump is not { } pump ||
            !_sentPumps.TryGetValue(pair.Key, out var sent) || sent != pump).ToArray();
        var writes = pending.Select(async pair =>
        {
            try
            {
                await controller.SendOwnedAsync(OwnerId, pair.Key, pair.Value).ConfigureAwait(false);
                return (Exception?)null;
            }
            catch (Exception error)
            {
                return error;
            }
        }).ToArray();
        var results = await Task.WhenAll(writes).ConfigureAwait(false);
        List<Exception> errors = [];
        for (var i = 0; i < pending.Length; i++)
        {
            if (results[i] is { } error)
            {
                errors.Add(error);
                continue;
            }
            var (id, output) = pending[i];
            _sentPumps[id] = output.Pump;
            _peakA = Math.Max(_peakA, output.ChannelA);
            _peakB = Math.Max(_peakB, output.ChannelB);
            _peakC = Math.Max(_peakC, output.ChannelC);
        }
        if (errors.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new LocalizedAggregateException("Reason.PipelineError", errors);
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == PipelineState.Running)
            {
                State = PipelineState.Paused;
                await controller.StopOwnedAsync(OwnerId, release: false);
                _sentPumps.Clear();
            }
            else if (State == PipelineState.Paused)
            {
                _lastTimestamp = controller.Clock.GetTimestamp();
                State = PipelineState.Running;
            }
        }
        catch (Exception error)
        {
            Error = error;
            State = PipelineState.Faulted;
            _cancellation?.Cancel();
            _cancellation = null;
            throw;
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public async Task StopAsync(string reason = "Reason.Manual")
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync(reason);
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    private async Task StopCoreAsync(string reason)
    {
        if (!HoldsResources)
            return;
        State = PipelineState.Stopping;
        _cancellation?.Cancel();
        _cancellation = null;
        try
        {
            await controller.StopOwnedAsync(OwnerId).ConfigureAwait(false);
            State = PipelineState.Idle;
            _runtime = null;
            if (!_recorded && _plan is not null && _startedAt != default)
            {
                _recorded = true;
                var record = new SessionRecord(Guid.NewGuid(), _startedAt, _elapsed.TotalSeconds, _plan.Name,
                    _plan.DeviceIds.Count, _peakA, _peakB, reason, ModeId: _plan.Id, ModeName: _plan.Name, PeakC: _peakC);
                if (Completed is { } handlers)
                    foreach (EventHandler<SessionRecord> handler in handlers.GetInvocationList())
                    {
                        try { handler(this, record); }
                        catch (Exception error) { System.Diagnostics.Trace.TraceError("Pipeline completion observer failed: {0}", error); }
                    }
            }
        }
        catch (Exception error)
        {
            State = PipelineState.Faulted;
            Error = error;
            throw;
        }
    }

    private void Notify()
    {
        if (Changed is not { } handlers)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Pipeline observer failed: {0}", error); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync("Reason.Close");
        await _loop.ConfigureAwait(false);
    }
}
