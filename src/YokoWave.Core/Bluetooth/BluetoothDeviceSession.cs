using System.Diagnostics;
using YokoWave.Core.Localization;

namespace YokoWave.Core.Bluetooth;

/// <summary>Owns one native link, its host driver, raw lease transitions, and ordered cleanup.</summary>
internal sealed class BluetoothDeviceSession
{
    private readonly DeviceDescriptor _device;
    private readonly IBluetoothConnection _link;
    private readonly Action<DeviceStatusUpdate> _publish;
    private readonly Action<DeviceTransportFault> _fault;
    private readonly Action<GattDeviceNotification> _rawNotification;
    // Startup owns the initial slot before the session is published in the transport's connection map.
    private readonly SemaphoreSlim _gate = new(0, 1);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private BluetoothProtocolDriver _driver;
    private volatile RawLease? _rawLease;
    private volatile bool _restoring;
    private volatile bool _closing;
    private volatile bool _closed;
    private int _generation;

    public Task Initialization => _initialized.Task;
    public bool IsClosing => _closing;

    public BluetoothDeviceSession(DeviceDescriptor device, BluetoothDeviceProfile profile, IBluetoothConnection link,
        Action<DeviceStatusUpdate> publish, Action<DeviceTransportFault> fault, Action<GattDeviceNotification> rawNotification)
    {
        _device = device;
        _link = link;
        _publish = publish;
        _fault = fault;
        _rawNotification = rawNotification;
        _driver = CreateDriver(profile);
        _driver.CommandRequested += OnCommandRequested;
        _link.NotificationReceived += OnPrimaryNotification;
        _link.AdditionalNotificationReceived += OnNotification;
        _link.Disconnected += OnDisconnected;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _driver.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _driver.ThrowIfFaulted();
            _publish(new(_device.Id, telemetry: new(MaximumWriteLength: _link.MaximumWriteLength)));
            _initialized.TrySetResult();
        }
        catch (Exception error)
        {
            _initialized.TrySetCanceled();
            List<Exception> cleanupErrors = [];
            try
            {
                await _driver.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            try
            {
                await CloseLinkAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
            if (cleanupErrors.Count > 0)
                throw new LocalizedAggregateException("Error.Cleanup", [error, .. cleanupErrors]);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DiscardAsync()
    {
        _initialized.TrySetCanceled();
        try
        {
            await CloseLinkAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> WithDriverAsync<T>(Func<BluetoothProtocolDriver, Task<T>> action, CancellationToken cancellationToken, bool restoreRaw = false)
    {
        await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closing)
                throw new LocalizedIOException("Error.BluetoothDisconnected");
            if (_rawLease is not null)
            {
                if (!restoreRaw)
                    throw new LocalizedOperationException("Error.RemoteRawOwned");
                await RestoreDriverAsync(cancellationToken).ConfigureAwait(false);
            }
            return await action(_driver).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task WithDriverAsync(Func<BluetoothProtocolDriver, Task> action, CancellationToken cancellationToken) =>
        WithDriverAsync(async driver =>
        {
            await action(driver).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task SendAsync(OutputFrame frame, CancellationToken cancellationToken) => WithDriverAsync(async driver =>
    {
        if (!double.IsFinite(frame.OutputLimit) || frame.OutputLimit is < 0 or > 100)
            throw new LocalizedArgumentException("Error.InvalidSettings");
        double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, frame.OutputLimit) : 0;
        frame = frame with
        {
            ChannelA = Math.Min(Clamp(frame.ChannelA), frame.Hardware?.ChannelALimit ?? 100),
            ChannelB = Math.Min(Clamp(frame.ChannelB), frame.Hardware?.ChannelBLimit ?? 100),
            ChannelC = Clamp(frame.ChannelC)
        };
        driver.Check(frame);
        if (frame == OutputFrame.Zero)
            await driver.StopAsync(cancellationToken).ConfigureAwait(false);
        else
            await driver.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken, restoreRaw: frame == OutputFrame.Zero);

    public async Task<GattDescription> OpenGattAsync(Guid lease, CancellationToken cancellationToken)
    {
        if (lease == Guid.Empty)
            throw new LocalizedArgumentException("Error.RemoteLease");
        await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closing || _rawLease is not null)
                throw new LocalizedOperationException("Error.DeviceInUse");
            await _driver.StopAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _generation);
            _rawLease = new(lease);
            return new(_device, Attributes(), _link.MaximumWriteLength);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<byte[]?> ReadGattAsync(Guid lease, GattAddress address, CancellationToken cancellationToken) => WithRawAsync(lease, () =>
    {
        if (!Attributes().Any(attribute => attribute.Address == address && attribute.Read))
            throw new LocalizedOperationException("Error.DeviceFeatures");
        return _link.ReadAsync(address, cancellationToken);
    }, cancellationToken);

    public Task WriteGattAsync(Guid lease, IReadOnlyList<GattWrite> writes, CancellationToken cancellationToken) => WithRawAsync(lease, async () =>
    {
        ValidateWrites(writes);
        await _link.WriteBatchAsync(writes, cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public async Task CloseGattAsync(Guid lease, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An emergency zero may already have restored the host driver.
            if (_rawLease is null)
                return;
            if (_rawLease.Id != lease)
                throw new LocalizedOperationException("Error.RemoteLease");
            await RestoreDriverAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _closing = true;
            Exception? shutdownError = null;
            try
            {
                if (_rawLease is not null)
                    await RestoreDriverAsync(cancellationToken).ConfigureAwait(false);
                await _driver.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                shutdownError = error;
            }
            try
            {
                await CloseLinkAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (shutdownError is not null)
            {
                throw new LocalizedAggregateException("Error.Cleanup", [shutdownError, cleanupError]);
            }
            if (shutdownError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(shutdownError).Throw();
        }
        finally
        {
            _gate.Release();
        }
    }

    private BluetoothProtocolDriver CreateDriver(BluetoothDeviceProfile profile) => BluetoothProtocolDriverFactory.Create(_device.Id, profile, _link,
        _publish, error => _fault(new(_device.Id, false, error)));

    private void OnPrimaryNotification(byte[] packet) => OnNotification(new(_driver.Profile.Notify, packet));

    private void OnNotification(GattNotification notification)
    {
        if (_closed)
            return;
        if (_rawLease is not null && !_restoring)
            _rawNotification(new(_device.Id, notification));
        else
            _driver.OnNotification(notification);
    }

    private void OnDisconnected()
    {
        if (!_closing)
            _fault(new(_device.Id, true, new LocalizedIOException("Error.BluetoothDisconnected")));
    }

    private void OnCommandRequested(DeviceCommand command) => _ = ExecuteRequestedCommandAsync(command, Volatile.Read(ref _generation));

    private async Task ExecuteRequestedCommandAsync(DeviceCommand command, int generation)
    {
        try
        {
            await Initialization.ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_closing && !_closed && _rawLease is null && generation == _generation)
                    await _driver.ExecuteCommandAsync(command, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!Initialization.IsCompletedSuccessfully)
        {
            // Initialization failed or this unused duplicate session was discarded.
        }
        catch (Exception error)
        {
            Trace.TraceError("Protocol maintenance failed: {0}", error);
        }
    }

    private IReadOnlyList<GattAttribute> Attributes() => _link.Attributes.Count > 0 ? _link.Attributes
        : _driver.Profile.Endpoints.Select(endpoint => new GattAttribute(endpoint.Address, endpoint.Read, endpoint.Write, endpoint.Write, endpoint.Notify)).ToArray();

    private async Task<T> WithRawAsync<T>(Guid lease, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closing || _restoring || _rawLease?.Id != lease)
                throw new LocalizedOperationException("Error.RemoteLease");
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateWrites(IReadOnlyList<GattWrite> writes)
    {
        if (writes is null || writes.Count is < 1 or > 16 || writes.Any(write => write is null) ||
            writes.Sum(write => (long)write.DelayAfterMilliseconds) > 100)
        {
            throw new LocalizedArgumentException("Error.ProtocolSettings");
        }
        foreach (var write in writes)
        {
            var attribute = Attributes().FirstOrDefault(attribute => attribute.Address == write.Address);
            if (attribute is null || !attribute.Write && !attribute.WriteWithoutResponse ||
                write.Mode == GattWriteMode.WithResponse && !attribute.Write || write.Mode == GattWriteMode.WithoutResponse && !attribute.WriteWithoutResponse ||
                !Enum.IsDefined(write.Mode) || write.Data is null || write.Data.Length < 1 || write.Data.Length > _link.MaximumWriteLength || write.DelayAfterMilliseconds is < 0 or > 100)
            {
                throw new LocalizedArgumentException("Error.ProtocolSettings");
            }
        }
    }

    private async Task RestoreDriverAsync(CancellationToken cancellationToken)
    {
        _restoring = true;
        Interlocked.Increment(ref _generation);
        _driver.CommandRequested -= OnCommandRequested;
        _driver = CreateDriver(_driver.Profile);
        _driver.CommandRequested += OnCommandRequested;
        await _driver.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _rawLease = null;
        _restoring = false;
    }

    private async ValueTask CloseLinkAsync()
    {
        _closing = true;
        _closed = true;
        _link.NotificationReceived -= OnPrimaryNotification;
        _link.AdditionalNotificationReceived -= OnNotification;
        _link.Disconnected -= OnDisconnected;
        _driver.CommandRequested -= OnCommandRequested;
        await _link.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record RawLease(Guid Id);
}
