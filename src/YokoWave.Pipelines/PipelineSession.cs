using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Pipelines;

public enum PipelineState
{
    Idle, Starting, Running, Paused, Stopping, Faulted
}

/// <summary>Serial background clock and device lifecycle for a compiled graph.</summary>
public sealed class PipelineSession : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<Guid, PipelineNodeStatus> _emptyStatus = new Dictionary<Guid, PipelineNodeStatus>();
    private readonly DeviceController _controller;
    private readonly bool _retainOwnership;
    private readonly PipelineServices _services;
    private readonly List<IRunParticipant> _participants = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;
    private PipelineRuntime? _runtime;
    private PipelinePlan? _plan;
    private IReadOnlyDictionary<Guid, PipelineNodeStatus> _lastNodeStatus = _emptyStatus;
    private long _lastTimestamp;
    private TimeSpan _elapsed;
    private PipelineOutputDispatcher _outputs;
    private bool _disposed;
    private DateTimeOffset _startedAt;
    private bool _recorded;
    public Guid OwnerId { get; }
    public IReadOnlyDictionary<Guid, PipelineNodeStatus> NodeStatus => _runtime?.NodeStatus ?? _lastNodeStatus;
    public PipelineInputs? Inputs => _runtime?.Inputs;
    public PipelineState State { get; private set; }
    public bool HoldsResources => State != PipelineState.Idle;
    public TimeSpan Elapsed => _elapsed;
    public TimeSpan? Remaining => _plan?.Duration is { } duration ? TimeSpan.FromTicks(Math.Max(0, (duration - _elapsed).Ticks)) : null;
    public Exception? Error { get; private set; }
    public PipelineFrame? LastFrame { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler<SessionRecord>? Completed;

    public PipelineSession(DeviceController controller, PipelineServices? services = null, Guid? ownerId = null, bool retainOwnership = false)
    {
        _controller = controller;
        _retainOwnership = retainOwnership;
        _services = services ?? new();
        OwnerId = ownerId ?? Guid.NewGuid();
        _outputs = new(controller, OwnerId);
        _controller.Faulted += OnDeviceFault;
    }

    public async Task StartAsync(PipelinePlan plan)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        bool starting = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HoldsResources)
                throw new LocalizedOperationException("Error.AlreadyRunning");
            State = PipelineState.Starting;
            starting = true;
            _cancellation = new();
            Notify();
            Error = null;
            _plan = plan;
            _elapsed = TimeSpan.Zero;
            _outputs = new(_controller, OwnerId);
            _recorded = false;
            _lastNodeStatus = _emptyStatus;
            _startedAt = default;
            LastFrame = null;
            List<PipelineDiagnostic> resourceErrors = [];
            PipelineCompiler.ValidateResources(plan.Nodes, _controller.Connections.ToDictionary(c => c.Key, c => c.Value.Device), resourceErrors);
            if (resourceErrors.Count > 0)
                throw new PipelineValidationException(resourceErrors);
            _runtime = new(plan, plan.DeviceIds.Where(id => _controller.AreSimulated([id])).ToHashSet(StringComparer.Ordinal));
            if (plan.DeviceIds.Count > 0)
            {
                if (_retainOwnership)
                {
                    if (plan.DeviceIds.Any(id => _controller.DeviceOwner(id) != OwnerId))
                        throw new LocalizedOperationException("Error.DeviceInUse");
                    await _controller.StopOwnedAsync(OwnerId, scope: plan.DeviceIds, release: false).ConfigureAwait(false);
                }
                else
                {
                    await _controller.AcquireTargetsAsync(OwnerId, plan.DeviceIds, allowInputs: true).ConfigureAwait(false);
                }
            }
            foreach (var node in plan.Nodes)
            {
                var context = new NodeRunContext(node.Id, OwnerId, _controller, _services);
                var participant = node.Registration.Participant(context, node.Configuration);
                if (participant is null)
                    continue;
                _participants.Add(participant);
                await participant.StartAsync(_cancellation.Token).ConfigureAwait(false);
            }
            _startedAt = _controller.Clock.GetUtcNow();
            _lastTimestamp = _controller.Clock.GetTimestamp();
            State = PipelineState.Running;
            await EvaluateAsync().ConfigureAwait(false);
            _cancellation.Token.ThrowIfCancellationRequested();
            var cancellation = _cancellation;
            var token = cancellation.Token;
            _loop = Task.Run(() => RunAsync(cancellation, token));
        }
        catch (Exception error)
        {
            if (!starting)
                throw;
            Error = error;
            try
            {
                await CleanupAsync().ConfigureAwait(false);
                _runtime = null;
                _plan = null;
                _startedAt = default;
                _lastNodeStatus = _emptyStatus;
                LastFrame = null;
                State = PipelineState.Idle;
            }
            catch (Exception cleanup)
            {
                State = PipelineState.Faulted;
                Error = new LocalizedAggregateException("Error.Cleanup", [error, cleanup]);
                throw Error;
            }
            _cancellation?.Dispose();
            _cancellation = null;
            throw;
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public bool SetConstant(Guid node, double value) => _runtime?.SetConstant(node, value) == true;
    public bool Update(NodeControl control) => _runtime?.Update(control) == true;
    public void SetLimit(Guid node, double value) => _runtime?.SetLimit(node, value);
    public async Task FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == PipelineState.Running)
                await EvaluateAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Error = error;
            try
            {
                await StopCoreAsync("Reason.PipelineError").ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]);
            }
            throw;
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == PipelineState.Running)
            {
                AccumulateElapsed();
                State = PipelineState.Paused;
                _runtime?.Suspend();
                foreach (var participant in _participants)
                    await participant.SetPausedAsync(true).ConfigureAwait(false);
                await _controller.StopOwnedAsync(OwnerId, scope: _plan?.DeviceIds, release: false).ConfigureAwait(false);
                _outputs.ResetPumpCommands();
            }
            else if (State == PipelineState.Paused)
            {
                _lastTimestamp = _controller.Clock.GetTimestamp();
                foreach (var participant in _participants)
                    await participant.SetPausedAsync(false).ConfigureAwait(false);
                State = PipelineState.Running;
            }
        }
        catch (Exception error)
        {
            Error = error;
            try
            {
                await StopCoreAsync("Reason.PipelineError").ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                Error = new LocalizedAggregateException("Error.Cleanup", [error, cleanup]);
                throw Error;
            }
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
        CancelPendingInput();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            await StopAsync("Reason.Close").ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
        }
        finally
        {
            _controller.Faulted -= OnDeviceFault;
        }
    }
    private void OnDeviceFault(object? sender, DeviceTransportFault fault)
    {
        if (!_disposed && HoldsResources && _plan?.DeviceIds.Contains(fault.DeviceId) == true)
        {
            CancelPendingInput();
            _ = StopFaultedAsync(fault);
        }
    }
    private async Task StopFaultedAsync(DeviceTransportFault fault)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!HoldsResources || _plan?.DeviceIds.Contains(fault.DeviceId) != true)
                return;
            Error = fault.Error;
            try
            {
                await StopCoreAsync("Reason.ConnectionError").ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                Error = new LocalizedAggregateException("Error.Cleanup", [fault.Error, cleanup]);
            }
        }
        finally
        {
            _gate.Release();
            Notify();
        }
    }

    private async Task RunAsync(CancellationTokenSource cancellation, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), _controller.Clock);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (_cancellation != cancellation || State is not (PipelineState.Running or PipelineState.Paused))
                        return;
                    AccumulateElapsed();
                    if (Remaining == TimeSpan.Zero)
                    {
                        if (_plan?.Duration is { } deadline)
                            _elapsed = deadline;
                        await StopCoreAsync("Reason.Timer").ConfigureAwait(false);
                        return;
                    }
                    if (State == PipelineState.Running)
                    {
                        await EvaluateAsync().ConfigureAwait(false);
                        if (_runtime?.CompletionReason is { } reason)
                        {
                            await StopCoreAsync(reason).ConfigureAwait(false);
                            return;
                        }
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
    }
    private async Task EvaluateAsync()
    {
        foreach (var participant in _participants)
            await participant.CaptureAsync(_runtime!.Inputs, _cancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
        LastFrame = _runtime!.Evaluate(_elapsed, _controller.Clock.GetUtcNow());
        if (_runtime.CompletionReason is null)
            await _outputs.SubmitAsync(LastFrame).ConfigureAwait(false);
    }

    private void CancelPendingInput()
    {
        try
        {
            Volatile.Read(ref _cancellation)?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    private async Task StopCoreAsync(string reason)
    {
        if (!HoldsResources)
            return;
        AccumulateElapsed();
        State = PipelineState.Stopping;
        var cancellation = _cancellation;
        cancellation?.Cancel();
        _cancellation = null;
        cancellation?.Dispose();
        try
        {
            await CleanupAsync().ConfigureAwait(false);
            State = PipelineState.Idle;
            _lastNodeStatus = _runtime?.NodeStatus.ToDictionary(p => p.Key, p => p.Value) ?? _emptyStatus;
            _runtime = null;
            if (!_recorded && _plan is not null && _startedAt != default)
            {
                _recorded = true;
                bool unknown = _plan.Bindings.Any(b => b.Kind == OutputBindingKind.Export);
                var contents = _plan.Nodes.Select(node =>
                {
                    var waveformId = _lastNodeStatus.GetValueOrDefault(node.Id)?.Waveform ?? (node.Configuration is WaveformNode wave ? wave.WaveformId : (Guid?)null);
                    string? waveformName = waveformId is { } id && _plan.Waveforms.TryGetValue(id, out var waveform) ? waveform.Name : null;
                    return new PipelineContentRecord(node.Id, node.Registration.Type, waveformId, waveformName);
                }).ToArray();
                var record = new SessionRecord(Guid.NewGuid(), _startedAt, _elapsed.TotalSeconds, _plan.Name,
                    _plan.DeviceIds.Count, unknown ? null : _outputs.PeakA, unknown ? null : _outputs.PeakB, reason, PipelineId: _plan.Id,
                    PeakC: unknown ? null : _outputs.PeakC, Contents: contents);
                if (Completed is { } handlers)
                {
                    foreach (EventHandler<SessionRecord> handler in handlers.GetInvocationList())
                    {
                        try
                        {
                            handler(this, record);
                        }
                        catch (Exception error)
                        {
                            System.Diagnostics.Trace.TraceError("Pipeline completion observer failed: {0}", error);
                        }
                    }
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

    private void AccumulateElapsed()
    {
        long now = _controller.Clock.GetTimestamp();
        if (State == PipelineState.Running && _startedAt != default)
        {
            _elapsed += _controller.Clock.GetElapsedTime(_lastTimestamp, now);
            if (_plan?.Duration is { } duration && _elapsed > duration)
                _elapsed = duration;
        }
        _lastTimestamp = now;
    }

    private void Notify()
    {
        if (Changed is not { } handlers)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Pipeline observer failed: {0}", error);
            }
        }
    }
    private async Task CleanupAsync()
    {
        List<Exception> errors = [];
        foreach (var resource in _participants.OfType<IOutputResource>())
        {
            try
            {
                await resource.StopOutputsAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        try
        {
            await _controller.StopOwnedAsync(OwnerId, scope: _plan?.DeviceIds, release: false).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        foreach (var participant in _participants.ToArray())
        {
            try
            {
                await participant.DisposeAsync().ConfigureAwait(false);
                _participants.Remove(participant);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        try
        {
            await _controller.StopOwnedAsync(OwnerId, scope: _plan?.DeviceIds, release: !_retainOwnership).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        if (errors.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
