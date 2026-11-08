using System.Collections.Concurrent;
using System.Diagnostics;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed class DeviceConnection(DeviceDescriptor device)
{
    public DeviceDescriptor Device { get; internal set; } = device;
    public ElectrodeContact ContactA { get; internal set; } = ElectrodeContact.Unknown;
    public ElectrodeContact ContactB { get; internal set; } = ElectrodeContact.Unknown;
    public OutputFrame Output { get; internal set; } = OutputFrame.Zero;
    public byte? HardwareVersion { get; internal set; }
    public byte? SoftwareVersion { get; internal set; }
    public bool? LightEnabled { get; internal set; }
    public MotionReading? Motion { get; internal set; }
    public int? Steps { get; internal set; }
    public DeviceTelemetry? Telemetry { get; internal set; }
    public DateTimeOffset? TelemetryCapturedAt { get; internal set; }
    internal long MotionSequence;
    internal bool MotionStreaming;
}

/// <summary>Serializes each device's output and lifecycle while independent devices can progress concurrently.</summary>
public sealed class DeviceController : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DeviceConnection> _connections = new();
    private readonly ConcurrentDictionary<string, Guid> _owners = new();
    private readonly DeviceOperationLocks _operations = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _disposeSync = new();
    private IDeviceTransport _transport;
    private volatile bool _disposed;
    private Task? _disposeTask;
    public IReadOnlyDictionary<string, DeviceConnection> Connections => _connections;
    public string TransportName => _transport.DisplayName;
    public bool IsSimulation => _transport.IsSimulation;
    public TimeProvider Clock { get; }
    public bool IsDeviceBusy(string id) => _owners.ContainsKey(id);
    public Guid? DeviceOwner(string id) => _owners.TryGetValue(id, out var owner) ? owner : null;
    public event EventHandler? Changed;
    public event EventHandler<DeviceTransportFault>? Faulted;

    public DeviceController(IDeviceTransport transport, TimeProvider? timeProvider = null)
    {
        _transport = transport;
        Clock = timeProvider ?? TimeProvider.System;
        Subscribe(transport, true);
    }

    private void Subscribe(IDeviceTransport transport, bool add)
    {
        if (transport is not IDeviceStatusSource source)
            return;
        if (add)
        {
            source.StatusChanged += OnStatusChanged;
            source.Faulted += OnFaulted;
        }
        else
        {
            source.StatusChanged -= OnStatusChanged;
            source.Faulted -= OnFaulted;
        }
    }

    public async Task ChangeTransportAsync(IDeviceTransport next)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            if (!_connections.IsEmpty)
                throw new LocalizedOperationException("Error.TransportConnected");
            var previous = _transport;
            Subscribe(previous, false);
            _transport = next;
            Subscribe(next, true);
            await previous.DisposeAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
        NotifyChanged();
    }

    private void OnStatusChanged(object? sender, DeviceStatusUpdate update)
    {
        if (!_disposed)
            _ = ApplyStatusAsync(sender, update, Clock.GetUtcNow());
    }

    private async Task ApplyStatusAsync(object? sender, DeviceStatusUpdate update, DateTimeOffset receivedAt)
    {
        try
        {
            using (await _operations.AcquireAsync([update.DeviceId]))
            {
                if (_disposed || sender != _transport || !_connections.TryGetValue(update.DeviceId, out var connection))
                    return;
                if (update.Battery is >= 0 and <= 100)
                    connection.Device = connection.Device with
                    {
                        Battery = update.Battery
                    };
                if (update.ContactA is { } a)
                    connection.ContactA = a;
                if (update.ContactB is { } b)
                    connection.ContactB = b;
                if (update.HardwareVersion is { } hardware)
                    connection.HardwareVersion = hardware;
                if (update.SoftwareVersion is { } software)
                    connection.SoftwareVersion = software;
                if (update.LightEnabled is { } light)
                    connection.LightEnabled = light;
                if (update.Steps is >= 0 and <= 65535)
                    connection.Steps = update.Steps;
                if (update.ChannelMask is >= 1 and <= 7)
                    connection.Device = connection.Device with
                    {
                        ChannelMask = update.ChannelMask
                    };
                if (update.Telemetry is { } telemetry)
                {
                    connection.Telemetry = (connection.Telemetry ?? new()).Merge(telemetry);
                    connection.TelemetryCapturedAt = receivedAt;
                }
                if (update.MotionAngle is { } angle && double.IsFinite(angle) && angle is >= 0 and <= 180)
                    connection.Motion = new(angle, ++connection.MotionSequence, receivedAt, connection.Steps);
            }
            NotifyChanged();
        }
        catch (Exception error) { Trace.TraceError("Device status callback failed: {0}", error); }
    }

    private void OnFaulted(object? sender, DeviceTransportFault fault)
    {
        if (!_disposed)
            _ = ApplyFaultAsync(sender, fault);
    }

    private async Task ApplyFaultAsync(object? sender, DeviceTransportFault fault)
    {
        try
        {
            using (await _operations.AcquireAsync([fault.DeviceId]))
            {
                if (_disposed || sender != _transport || !_connections.ContainsKey(fault.DeviceId))
                    return;
                if (fault.Disconnected)
                {
                    try
                    {
                        await _transport.DisconnectAsync(fault.DeviceId);
                    }
                    catch (Exception error) { Trace.TraceWarning("Lost link cleanup failed: {0}", error); }
                    _connections.TryRemove(fault.DeviceId, out _);
                    _owners.TryRemove(fault.DeviceId, out _);
                }
            }
            if (_disposed)
                return;
            NotifyChanged();
            if (Faulted is { } handlers)
                foreach (EventHandler<DeviceTransportFault> handler in handlers.GetInvocationList())
                    try
                    {
                        handler(this, fault);
                    }
                    catch (Exception error) { Trace.TraceError("Device fault subscriber failed: {0}", error); }
        }
        catch (Exception error) { Trace.TraceError("Device fault callback failed: {0}", error); }
    }

    private void NotifyChanged()
    {
        if (Changed is not { } handlers)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception error) { Trace.TraceError("Device change subscriber failed: {0}", error); }
    }

    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            return await _transport.ScanAsync(ct);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task AcquireTargetsAsync(Guid owner, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (owner == Guid.Empty || ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace))
            throw new LocalizedOperationException("Error.SelectConnected");
        var targets = ids.Distinct(StringComparer.Ordinal).ToArray();
        using (await _operations.AcquireAsync(targets, ct))
        {
            ThrowIfDisposed();
            if (targets.Any(id => !_connections.ContainsKey(id)))
                throw new LocalizedOperationException("Error.SelectConnected");
            if (targets.Any(id => Get(id).Device.IsInput))
                throw new LocalizedOperationException("Error.SensorOnly");
            if (targets.Any(id => DeviceOwner(id) is { } current && current != owner))
                throw new LocalizedOperationException("Error.DeviceInUse");
            foreach (var id in targets)
                _owners[id] = owner;
            try
            {
                await Task.WhenAll(targets.Select(id => WriteCoreAsync(Get(id), OutputFrame.Zero, ct)));
            }
            catch (Exception error)
            {
                List<Exception> cleanupErrors = [];
                foreach (var id in targets)
                {
                    try
                    {
                        await WriteCoreAsync(Get(id), OutputFrame.Zero, CancellationToken.None);
                        _owners.TryRemove(id, out _);
                    }
                    catch (Exception cleanupError) { cleanupErrors.Add(cleanupError); }
                }
                // A failed stop keeps its lease, and neither the initiating error nor cleanup errors are lost.
                if (cleanupErrors.Count > 0)
                    throw new LocalizedAggregateException("Error.StopUnconfirmed", [error, .. cleanupErrors]);
                throw;
            }
        }
        NotifyChanged();
    }

    public async Task SendOwnedAsync(Guid owner, string id, OutputFrame frame, CancellationToken ct = default)
    {
        using (await _operations.AcquireAsync([id], ct))
        {
            ThrowIfDisposed();
            if (DeviceOwner(id) != owner)
                throw new LocalizedOperationException("Error.DeviceInUse");
            await WriteCoreAsync(Get(id), frame, ct);
        }
        NotifyChanged();
    }

    public async Task StopOwnedAsync(Guid owner, CancellationToken ct = default)
    {
        var targets = _owners.Where(pair => pair.Value == owner).Select(pair => pair.Key).ToArray();
        var errors = await Task.WhenAll(targets.Select(id => CaptureErrorAsync(async () =>
        {
            using (await _operations.AcquireAsync([id], ct))
            {
                if (DeviceOwner(id) != owner)
                    return;
                if (_connections.TryGetValue(id, out var connection))
                {
                    await WriteCoreAsync(connection, OutputFrame.Zero, ct);
                    if (connection.MotionStreaming && _transport is IElectricalMotionSource motion)
                        await motion.SetMotionStreamingAsync(id, false, ct);
                    connection.MotionStreaming = false;
                }
                _owners.TryRemove(id, out _);
            }
        })));
        NotifyChanged();
        ThrowErrors(errors, "Error.StopUnconfirmed");
    }

    public async Task StartMotionOwnedAsync(Guid owner, string id)
    {
        using (await _operations.AcquireAsync([id]))
        {
            ThrowIfDisposed();
            if (DeviceOwner(id) != owner)
                throw new LocalizedOperationException("Error.DeviceInUse");
            var connection = Get(id);
            if (!IsSimulation && connection.Device.Profile?.Motion != true)
                throw new LocalizedOperationException("Error.MotionUnavailable");
            connection.Motion = null;
            if (_transport is IElectricalMotionSource motion)
            {
                connection.MotionStreaming = true;
                await motion.SetMotionStreamingAsync(id, true);
            }
            else if (!IsSimulation)
                throw new LocalizedOperationException("Error.MotionUnavailable");
            connection.MotionStreaming = true;
        }
    }

    public async Task SetSimulatedMotionAsync(Guid owner, double angle)
    {
        if (!IsSimulation || !double.IsFinite(angle) || angle is < 0 or > 180)
            throw new LocalizedArgumentException("Error.MotionInput");
        var targets = _owners.Where(pair => pair.Value == owner).Select(pair => pair.Key).ToArray();
        using (await _operations.AcquireAsync(targets))
        {
            ThrowIfDisposed();
            if (!IsSimulation)
                throw new LocalizedArgumentException("Error.MotionInput");
            foreach (var id in targets)
            {
                if (DeviceOwner(id) != owner || !_connections.TryGetValue(id, out var connection))
                    continue;
                connection.Motion = new(angle, ++connection.MotionSequence, Clock.GetUtcNow());
            }
        }
    }

    public async Task RefreshFeaturesAsync(string id, CancellationToken ct = default)
    {
        using (await _operations.AcquireAsync([id], ct))
        {
            ThrowIfDisposed();
            var connection = Get(id);
            if (IsDeviceBusy(id))
                throw new LocalizedOperationException("Error.DeviceInUse");
            if (connection.Device.Profile?.Light != true || _transport is not IElectricalDeviceFeatures features)
                throw new LocalizedOperationException("Error.DeviceFeatures");
            var state = await features.RefreshFeaturesAsync(id, ct);
            connection.HardwareVersion = state.HardwareVersion;
            connection.SoftwareVersion = state.SoftwareVersion;
            connection.LightEnabled = state.LightEnabled;
        }
        NotifyChanged();
    }

    public async Task ResetInputAsync(string id, CancellationToken ct = default)
    {
        using (await _operations.AcquireAsync([id], ct))
        {
            ThrowIfDisposed();
            var connection = Get(id);
            if (IsDeviceBusy(id) || connection.Device.Profile?.Protocol != BluetoothProtocol.Civet || _transport is not IInputDeviceFeatures features)
                throw new LocalizedOperationException("Error.DeviceFeatures");
            await features.ResetInputAsync(id, ct);
        }
    }

    public async Task SetLightAsync(string id, bool enabled, CancellationToken ct = default)
    {
        using (await _operations.AcquireAsync([id], ct))
        {
            ThrowIfDisposed();
            var connection = Get(id);
            if (IsDeviceBusy(id))
                throw new LocalizedOperationException("Error.DeviceInUse");
            if (connection.Device.Profile?.Light != true || connection.HardwareVersion is not > 0 || connection.SoftwareVersion is not > 0 ||
                _transport is not IElectricalDeviceFeatures features)
                throw new LocalizedOperationException("Error.DeviceFeatures");
            connection.LightEnabled = await features.SetLightAsync(id, enabled, ct);
        }
        NotifyChanged();
    }

    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            using (await _operations.AcquireAsync([device.Id], ct))
            {
                ThrowIfDisposed();
                if (_connections.ContainsKey(device.Id))
                    return;
                await _transport.ConnectAsync(device, ct);
                try
                {
                    await _transport.SendAsync(device.Id, OutputFrame.Zero, ct);
                }
                catch (Exception error)
                {
                    try
                    {
                        await _transport.DisconnectAsync(device.Id, CancellationToken.None);
                    }
                    catch (Exception cleanupError)
                    {
                        throw new LocalizedAggregateException("Error.Cleanup", [error, cleanupError]);
                    }
                    throw;
                }
                _connections.TryAdd(device.Id, new(device));
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
        NotifyChanged();
    }

    public async Task SendAsync(string id, OutputFrame frame, CancellationToken ct = default)
    {
        using (await _operations.AcquireAsync([id], ct))
        {
            ThrowIfDisposed();
            if (IsDeviceBusy(id))
                throw new LocalizedOperationException("Error.DeviceInUse");
            await WriteCoreAsync(Get(id), frame, ct);
        }
        NotifyChanged();
    }

    private async Task WriteCoreAsync(DeviceConnection connection, OutputFrame frame, CancellationToken ct)
    {
        var output = frame with
        {
            ChannelA = connection.Device.HasChannel(0) ? Clamp(frame.ChannelA) : 0,
            ChannelB = connection.Device.HasChannel(1) ? Clamp(frame.ChannelB) : 0,
            ChannelC = connection.Device.HasChannel(2) ? Clamp(frame.ChannelC) : 0,
            Frequency = double.IsFinite(frame.Frequency) ? Math.Clamp(frame.Frequency, 1, 100) : 1
        };
        await _transport.SendAsync(connection.Device.Id, output, ct);
        connection.Output = output;
    }

    public Task StopAllAsync(CancellationToken ct = default) => StopAllCoreAsync(ct);
    private async Task StopAllCoreAsync(CancellationToken ct = default)
    {
        var errors = await Task.WhenAll(_connections.Keys.Select(id => CaptureErrorAsync(async () =>
        {
            using (await _operations.AcquireAsync([id], ct))
                if (_connections.TryGetValue(id, out var connection))
                    await WriteCoreAsync(connection, OutputFrame.Zero, ct);
        })));
        NotifyChanged();
        ThrowErrors(errors, "Error.StopUnconfirmed");
    }

    public async Task DisconnectAsync(string id, CancellationToken ct = default)
    {
        Exception? stopError, disconnectError;
        using (await _operations.AcquireAsync([id], ct))
        {
            if (!_connections.TryGetValue(id, out var connection))
                return;
            stopError = await CaptureErrorAsync(() => WriteCoreAsync(connection, OutputFrame.Zero, ct));
            disconnectError = await CaptureErrorAsync(() => _transport.DisconnectAsync(id, CancellationToken.None));
            _connections.TryRemove(id, out _);
            _owners.TryRemove(id, out _);
        }
        NotifyChanged();
        ThrowErrors([stopError, disconnectError], "Error.Cleanup");
    }

    private DeviceConnection Get(string id) => _connections.TryGetValue(id, out var item)
        ? item : throw new LocalizedOperationException("Error.NotConnected");
    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static async Task<Exception?> CaptureErrorAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error) { return error; }
    }
    private static void ThrowErrors(IEnumerable<Exception?> errors, string key)
    {
        var failures = errors.OfType<Exception>().ToArray();
        if (failures.Length > 0)
            throw new LocalizedAggregateException(key, failures);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await _lifecycleGate.WaitAsync();
        List<Exception?> errors = [];
        try
        {
            _disposed = true;
            Subscribe(_transport, false);
            errors.Add(await CaptureErrorAsync(() => StopAllCoreAsync()));
            foreach (var id in _connections.Keys.ToArray())
                errors.Add(await CaptureErrorAsync(() => DisconnectAsync(id)));
            errors.Add(await CaptureErrorAsync(() => _transport.DisposeAsync().AsTask()));
            // Gates use no native wait handles. Keep them valid until queued notifications and callers have drained.
        }
        finally
        {
            _lifecycleGate.Release();
        }
        ThrowErrors(errors, "Error.Cleanup");
    }
}
