using System.Collections.Concurrent;
using System.Diagnostics;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

/// <summary>Serializes each device's output and lifecycle while independent devices can progress concurrently.</summary>
public sealed class DeviceController : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DeviceConnection> _connections = new();
    private readonly ConcurrentDictionary<string, Guid> _owners = new();
    private readonly ConcurrentDictionary<string, DeviceOutputSettings> _outputSettings = new(StringComparer.Ordinal);
    private readonly DeviceOperationLocks _operations = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly RoutedDeviceTransport _transport;
    private volatile bool _disposed;
    private Task? _disposeTask;
    public IRawGattTransport RawGatt => _transport;
    public IReadOnlyDictionary<string, DeviceConnection> Connections => _connections;
    public string TransportName => _transport.DisplayName;
    public bool IsSimulation => _transport.IsSimulation;
    public bool IsLocalSimulation => _transport.IsLocalSimulation;
    public TimeProvider Clock { get; }
    public IReadOnlyList<DeviceOutputSettings> OutputSettings => _outputSettings.Values.OrderBy(s => s.DeviceId, StringComparer.Ordinal).ToArray();
    public event EventHandler? Changed;
    public event EventHandler<DeviceTransportFault>? Faulted;

    public DeviceController(IDeviceTransport transport, TimeProvider? timeProvider = null)
    {
        _transport = transport as RoutedDeviceTransport ?? new(transport);
        Clock = timeProvider ?? TimeProvider.System;
        Subscribe(_transport, true);
    }
    public bool AreSimulated(IEnumerable<string> ids) => ids.All(id => _connections.TryGetValue(id, out var connection)
        ? connection.Device.Simulated ?? _transport.IsSimulated(id) : IsSimulation);
    public bool IsDeviceBusy(string id) => _owners.ContainsKey(id);
    public Guid? DeviceOwner(string id) => _owners.TryGetValue(id, out var owner) ? owner : null;

    public async Task ChangeTransportAsync(IDeviceTransport next)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_connections.IsEmpty)
                throw new LocalizedOperationException("Error.TransportConnected");
            await _transport.ReplaceLocalAsync(next).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
        NotifyChanged();
    }

    public async Task AddSourceAsync(string key, IDeviceTransport source, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _transport.Add(key, source);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
    public IReadOnlyList<string> SourceDeviceIds(string key) => _transport.DeviceIds(key);
    public async Task RemoveSourceAsync(string key, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            List<Exception> errors = [];
            foreach (string id in _transport.DeviceIds(key))
            {
                try
                {
                    await DisconnectAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
            try
            {
                await _transport.RemoveAsync(key).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            if (errors.Count > 0)
                throw new LocalizedAggregateException("Error.Cleanup", errors);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await _transport.ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task AcquireTargetsAsync(Guid owner, IReadOnlyList<string> ids, CancellationToken cancellationToken = default, bool allowInputs = false)
    {
        if (owner == Guid.Empty || ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace))
            throw new LocalizedOperationException("Error.SelectConnected");
        string[] targets = ids.Distinct(StringComparer.Ordinal).ToArray();
        using (await _operations.AcquireAsync(targets, cancellationToken).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            if (targets.Any(id => !_connections.ContainsKey(id)))
                throw new LocalizedOperationException("Error.SelectConnected");
            if (!allowInputs && targets.Any(id => Get(id).Device.IsInput))
                throw new LocalizedOperationException("Error.SensorOnly");
            if (targets.Any(id => DeviceOwner(id) is { } current && current != owner))
                throw new LocalizedOperationException("Error.DeviceInUse");
            foreach (string? id in targets)
                _owners[id] = owner;
            try
            {
                await Task.WhenAll(targets.Select(id => StopConnectionAsync(Get(id), cancellationToken))).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                List<Exception> cleanupErrors = [];
                foreach (string? id in targets)
                {
                    try
                    {
                        await StopConnectionAsync(Get(id), CancellationToken.None).ConfigureAwait(false);
                        _owners.TryRemove(id, out _);
                    }
                    catch (Exception cleanupError)
                    {
                        cleanupErrors.Add(cleanupError);
                    }
                }
                // A failed stop keeps its lease, and neither the initiating error nor cleanup errors are lost.
                if (cleanupErrors.Count > 0)
                    throw new LocalizedAggregateException("Error.StopUnconfirmed", [error, .. cleanupErrors]);
                throw;
            }
        }
        NotifyChanged();
    }

    public async Task SendOwnedAsync(Guid owner, string id, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            if (DeviceOwner(id) != owner)
                throw new LocalizedOperationException("Error.DeviceInUse");
            await WriteCoreAsync(Get(id), frame, cancellationToken).ConfigureAwait(false);
        }
        NotifyChanged();
    }

    public async Task StopOwnedAsync(Guid owner, CancellationToken cancellationToken = default, IReadOnlyList<string>? scope = null, bool release = true)
    {
        string[] targets = _owners.Where(pair => pair.Value == owner && (scope is null || scope.Contains(pair.Key))).Select(pair => pair.Key).ToArray();
        var errors = await Task.WhenAll(targets.Select(id => CaptureErrorAsync(async () =>
        {
            using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
            {
                if (DeviceOwner(id) != owner)
                    return;
                if (_connections.TryGetValue(id, out var connection))
                {
                    await StopConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                if (release)
                    _owners.TryRemove(id, out _);
            }
        }))).ConfigureAwait(false);
        NotifyChanged();
        ThrowErrors(errors, "Error.StopUnconfirmed");
    }

    public async Task StartMotionOwnedAsync(Guid owner, string id)
    {
        using (await _operations.AcquireAsync([id]).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            if (DeviceOwner(id) != owner)
                throw new LocalizedOperationException("Error.DeviceInUse");
            var connection = Get(id);
            if (!AreSimulated([id]) && connection.Device.Profile?.Motion != true)
                throw new LocalizedOperationException("Error.MotionUnavailable");
            if (GetOutputSettings(id).Hardware?.Accepts(connection.Device, needsMotion: true) == false)
                throw new LocalizedOperationException("Error.ProtocolSettings");
            connection.ResetMotion();
            if (_transport is IElectricalMotionSource motion)
            {
                connection.MotionStreaming = true;
                await motion.SetMotionStreamingAsync(id, true).ConfigureAwait(false);
            }
            else if (!AreSimulated([id]))
            {
                throw new LocalizedOperationException("Error.MotionUnavailable");
            }

            connection.MotionStreaming = true;
        }
    }

    public async Task SetSimulatedMotionAsync(Guid owner, double angle)
    {
        if (!double.IsFinite(angle) || angle is < 0 or > 180)
            throw new LocalizedArgumentException("Error.MotionInput");
        string[] targets = _owners.Where(pair => pair.Value == owner).Select(pair => pair.Key).ToArray();
        using (await _operations.AcquireAsync(targets).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            if (!AreSimulated(targets))
                throw new LocalizedArgumentException("Error.MotionInput");
            foreach (string? id in targets)
            {
                if (DeviceOwner(id) != owner || !_connections.TryGetValue(id, out var connection))
                    continue;
                connection.SetMotion(angle, Clock.GetUtcNow());
            }
        }
    }

    public async Task RefreshFeaturesAsync(string id, CancellationToken cancellationToken = default)
    {
        using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            var connection = Get(id);
            if (IsDeviceBusy(id))
                throw new LocalizedOperationException("Error.DeviceInUse");
            if (connection.Device.Profile?.Light != true || _transport is not IElectricalDeviceFeatures features)
                throw new LocalizedOperationException("Error.DeviceFeatures");
            var state = await features.RefreshFeaturesAsync(id, cancellationToken).ConfigureAwait(false);
            connection.ApplyFeatures(state);
        }
        NotifyChanged();
    }

    public async Task ExecuteCommandAsync(string id, DeviceCommand command, Guid? owner = null, CancellationToken cancellationToken = default)
    {
        command.Validate();
        using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            var connection = Get(id);
            if (!command.ReadOnly && DeviceOwner(id) is { } current && current != owner)
                throw new LocalizedOperationException("Error.DeviceInUse");
            if (connection.Device.Profile?.Supports(command.Capability) != true || _transport is not IDeviceCommandTransport transport)
                throw new LocalizedOperationException("Error.DeviceFeatures");
            if (command is ConfigureMotionStream { Enabled: true } && GetOutputSettings(id).Hardware?.Accepts(connection.Device, needsMotion: true) == false)
                throw new LocalizedOperationException("Error.ProtocolSettings");
            await transport.ExecuteCommandAsync(id, command, cancellationToken).ConfigureAwait(false);
            if (command is ConfigureMotionStream motion)
                connection.MotionStreaming = motion.Enabled;
        }
        NotifyChanged();
    }

    public async Task ResetInputAsync(string id, CancellationToken cancellationToken = default)
    {
        using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            var connection = Get(id);
            if (IsDeviceBusy(id) || connection.Device.Profile?.Protocol != BluetoothProtocol.Civet || _transport is not IInputDeviceFeatures features)
                throw new LocalizedOperationException("Error.DeviceFeatures");
            await features.ResetInputAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetLightAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            var connection = Get(id);
            if (IsDeviceBusy(id))
                throw new LocalizedOperationException("Error.DeviceInUse");
            if (connection.Device.Profile?.Light != true || connection.HardwareVersion is not > 0 || connection.SoftwareVersion is not > 0 ||
                _transport is not IElectricalDeviceFeatures features)
            {
                throw new LocalizedOperationException("Error.DeviceFeatures");
            }

            connection.LightEnabled = await features.SetLightAsync(id, enabled, cancellationToken).ConfigureAwait(false);
        }
        NotifyChanged();
    }

    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (await _operations.AcquireAsync([device.Id], cancellationToken).ConfigureAwait(false))
            {
                ThrowIfDisposed();
                if (_connections.ContainsKey(device.Id))
                    return;
                await _transport.ConnectAsync(device, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _transport.SendAsync(device.Id, OutputFrame.Zero, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    try
                    {
                        await _transport.DisconnectAsync(device.Id, CancellationToken.None).ConfigureAwait(false);
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

    public async Task SendAsync(string id, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            if (IsDeviceBusy(id))
                throw new LocalizedOperationException("Error.DeviceInUse");
            await WriteCoreAsync(Get(id), frame, cancellationToken).ConfigureAwait(false);
        }
        NotifyChanged();
    }

    public Task StopAllAsync(CancellationToken cancellationToken = default) => StopAllCoreAsync(cancellationToken);

    public async Task DisconnectAsync(string id, CancellationToken cancellationToken = default)
    {
        Exception? stopError, disconnectError;
        using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
        {
            if (!_connections.TryGetValue(id, out var connection))
                return;
            stopError = await CaptureErrorAsync(() => WriteCoreAsync(connection, OutputFrame.Zero, cancellationToken)).ConfigureAwait(false);
            disconnectError = await CaptureErrorAsync(() => _transport.DisconnectAsync(id, CancellationToken.None)).ConfigureAwait(false);
            _connections.TryRemove(id, out _);
            _owners.TryRemove(id, out _);
        }
        NotifyChanged();
        ThrowErrors([stopError, disconnectError], "Error.Cleanup");
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new(_disposeTask ??= DisposeCoreAsync());
    }
    public DeviceOutputSettings GetOutputSettings(string id) => _outputSettings.GetValueOrDefault(id) ?? new(id);
    public void LoadOutputSettings(IEnumerable<DeviceOutputSettings> settings)
    {
        _outputSettings.Clear();
        foreach (var value in settings)
        {
            value.Validate();
            _outputSettings[value.DeviceId] = value;
        }
    }
    public async Task SetOutputSettingsAsync(DeviceDescriptor device, DeviceOutputSettings settings, CancellationToken cancellationToken = default, Guid? owner = null)
    {
        settings.Validate();
        using (await _operations.AcquireAsync([device.Id], cancellationToken).ConfigureAwait(false))
        {
            ThrowIfDisposed();
            if (DeviceOwner(device.Id) is { } current && current != owner)
                throw new LocalizedOperationException("Error.DeviceInUse");
            if (settings.DeviceId != device.Id || !settings.Accepts(device, _connections.TryGetValue(device.Id, out var connected) && connected.MotionStreaming))
                throw new LocalizedArgumentException("Error.ProtocolSettings");
            var previous = _outputSettings.GetValueOrDefault(device.Id);
            _outputSettings[device.Id] = settings;
            try
            {
                if (_connections.TryGetValue(device.Id, out var connection))
                    await WriteCoreAsync(connection, OutputFrame.Zero, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (previous is null)
                    _outputSettings.TryRemove(device.Id, out _);
                else
                    _outputSettings[device.Id] = previous;
                throw;
            }
        }
        NotifyChanged();
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

    private void OnStatusChanged(object? sender, DeviceStatusUpdate update)
    {
        if (!_disposed)
            _ = ApplyStatusAsync(sender, update, Clock.GetUtcNow());
    }

    private async Task ApplyStatusAsync(object? sender, DeviceStatusUpdate update, DateTimeOffset receivedAt)
    {
        try
        {
            using (await _operations.AcquireAsync([update.DeviceId]).ConfigureAwait(false))
            {
                if (_disposed || sender != _transport || !_connections.TryGetValue(update.DeviceId, out var connection))
                    return;
                connection.ApplyStatus(update, receivedAt);
            }
            NotifyChanged();
        }
        catch (Exception error)
        {
            Trace.TraceError("Device status callback failed: {0}", error);
        }
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
            using (await _operations.AcquireAsync([fault.DeviceId]).ConfigureAwait(false))
            {
                if (_disposed || sender != _transport || !_connections.ContainsKey(fault.DeviceId))
                    return;
                if (fault.Disconnected)
                {
                    try
                    {
                        await _transport.DisconnectAsync(fault.DeviceId).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        Trace.TraceWarning("Lost link cleanup failed: {0}", error);
                    }
                    _connections.TryRemove(fault.DeviceId, out _);
                    _owners.TryRemove(fault.DeviceId, out _);
                }
            }
            if (_disposed)
                return;
            NotifyChanged();
            if (Faulted is { } handlers)
            {
                foreach (EventHandler<DeviceTransportFault> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, fault);
                    }
                    catch (Exception error)
                    {
                        Trace.TraceError("Device fault subscriber failed: {0}", error);
                    }
                }
            }
        }
        catch (Exception error)
        {
            Trace.TraceError("Device fault callback failed: {0}", error);
        }
    }

    private void NotifyChanged()
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
                Trace.TraceError("Device change subscriber failed: {0}", error);
            }
        }
    }

    private async Task WriteCoreAsync(DeviceConnection connection, OutputFrame frame, CancellationToken cancellationToken)
    {
        frame = ApplyOutputSettings(connection.Device, frame);
        if (!double.IsFinite(frame.OutputLimit) || frame.OutputLimit is < 0 or > 100)
            throw new LocalizedArgumentException("Error.InvalidSettings");
        frame.Hardware?.Validate();
        var output = frame with
        {
            ChannelA = connection.Device.HasChannel(0) ? Math.Min(Clamp(frame.ChannelA), Math.Min(frame.OutputLimit, frame.Hardware?.ChannelALimit ?? 100)) : 0,
            ChannelB = connection.Device.HasChannel(1) ? Math.Min(Clamp(frame.ChannelB), Math.Min(frame.OutputLimit, frame.Hardware?.ChannelBLimit ?? 100)) : 0,
            ChannelC = connection.Device.HasChannel(2) ? Math.Min(Clamp(frame.ChannelC), frame.OutputLimit) : 0,
            Frequency = double.IsFinite(frame.Frequency) ? Math.Clamp(frame.Frequency, 1, 100) : 1
        };
        output.NativeProgram?.ValidateOutput(output);
        await _transport.SendAsync(connection.Device.Id, output, cancellationToken).ConfigureAwait(false);
        connection.Output = output;
    }
    private async Task StopConnectionAsync(DeviceConnection connection, CancellationToken cancellationToken)
    {
        string id = connection.Device.Id;
        List<Exception> errors = [];
        try
        {
            await WriteCoreAsync(connection, OutputFrame.Zero, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        if (connection.Device.IsInput && connection.Device.Profile?.Supports(DeviceCapability.SensorStream) == true)
        {
            try
            {
                await _transport.ExecuteCommandAsync(id, new ConfigureSensorStream(false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        if (connection.MotionStreaming)
        {
            try
            {
                await _transport.SetMotionStreamingAsync(id, false, cancellationToken).ConfigureAwait(false);
                connection.MotionStreaming = false;
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        if (errors.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        ThrowErrors(errors, "Error.StopUnconfirmed");
    }
    private async Task StopAllCoreAsync(CancellationToken cancellationToken = default)
    {
        var errors = await Task.WhenAll(_connections.Keys.Select(id => CaptureErrorAsync(async () =>
        {
            using (await _operations.AcquireAsync([id], cancellationToken).ConfigureAwait(false))
            {
                if (_connections.TryGetValue(id, out var connection))
                    await StopConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }))).ConfigureAwait(false);
        NotifyChanged();
        ThrowErrors(errors, "Error.StopUnconfirmed");
    }

    private DeviceConnection Get(string id) => _connections.TryGetValue(id, out var item)
        ? item : throw new LocalizedOperationException("Error.NotConnected");
    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static async Task<Exception?> CaptureErrorAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }
    private static void ThrowErrors(IEnumerable<Exception?> errors, string key)
    {
        var failures = errors.OfType<Exception>().ToArray();
        if (failures.Length > 0)
            throw new LocalizedAggregateException(key, failures);
    }
    private async Task DisposeCoreAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        List<Exception?> errors = [];
        try
        {
            _disposed = true;
            Subscribe(_transport, false);
            errors.Add(await CaptureErrorAsync(() => StopAllCoreAsync()).ConfigureAwait(false));
            foreach (string? id in _connections.Keys.ToArray())
                errors.Add(await CaptureErrorAsync(() => DisconnectAsync(id)).ConfigureAwait(false));
            errors.Add(await CaptureErrorAsync(() => _transport.DisposeAsync().AsTask()).ConfigureAwait(false));
            // Gates use no native wait handles. Keep them valid until queued notifications and callers have drained.
        }
        finally
        {
            _lifecycleGate.Release();
        }
        ThrowErrors(errors, "Error.Cleanup");
    }
    private OutputFrame ApplyOutputSettings(DeviceDescriptor device, OutputFrame frame)
    {
        if (frame == OutputFrame.Zero)
            return frame;
        if (!_outputSettings.TryGetValue(device.Id, out var settings))
            return frame;
        if (!settings.Accepts(device))
            throw new LocalizedArgumentException("Error.ProtocolSettings");
        return frame with { Gentle = settings.Gentle, Hardware = settings.Hardware, DgBalances = settings.Balances };
    }
}
