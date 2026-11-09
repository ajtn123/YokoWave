using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>A server mode reserves physical devices. Peer leases divide that reservation without transferring it.</summary>
public sealed class RemoteExportSession : IAsyncDisposable
{
    private readonly DeviceController _devices;
    private readonly Dictionary<string, string> _exports;
    private readonly Dictionary<Guid, RemoteDeviceLease> _leases = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _clock = new();
    private readonly TimeSpan? _duration;
    private Task _loop = Task.CompletedTask;
    private volatile bool _running;
    private int _disposed;
    private int _started;
    private readonly Guid _owner;
    private readonly bool _retainOwnership;
    private readonly DeviceController? _controlDevices;
    private readonly RemoteLeaseType? _allowedLease;
    private bool _paused;
    public Guid Id { get; }
    public RemoteServerSettings Settings { get; }
    public bool IsRunning => _running;
    public Task Completion => _loop;
    public TimeSpan Elapsed => _clock.Elapsed;
    public TimeSpan? Duration => _duration;
    public TimeSpan? Remaining => _duration is { } duration ? TimeSpan.FromSeconds(Math.Max(0, (duration - Elapsed).TotalSeconds)) : null;
    public event Action<Exception>? Faulted;
    internal void ReportFault(Exception error) => Faulted?.Invoke(error);
    public RemoteExportSession(Guid id, DeviceController devices, IReadOnlyList<string> targets, RemoteServerSettings settings, TimeSpan? duration = null,
        Guid? reservationOwner = null, DeviceController? controlDevices = null, RemoteLeaseType? allowedLease = null)
    {
        settings.Validate();
        if (id == Guid.Empty || targets.Count > 32 || duration is { } limit && (limit.TotalSeconds < 1 || limit.TotalSeconds > 7200))
            throw new LocalizedArgumentException("Error.RemoteSettings");
        Id = id;
        _owner = reservationOwner ?? id;
        _retainOwnership = reservationOwner.HasValue;
        _controlDevices = controlDevices;
        _allowedLease = allowedLease;
        _devices = devices;
        Settings = settings;
        _duration = duration;
        _exports = targets.Distinct().ToDictionary(LocalExportId, value => value);
    }
    private string LocalExportId(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Id:N}/{id}")))[..24].ToLowerInvariant();
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new LocalizedOperationException("Error.AlreadyRunning");
        if (_exports.Count == 0 || _exports.Values.Any(id => !_devices.Connections.TryGetValue(id, out var connection) ||
            connection.Device.Origin != DeviceOrigin.Local || connection.Device.Profile is not { Supported: true } || connection.Device.Simulated == true))
            throw new LocalizedOperationException("Error.RemoteLocalDevices");
        if (_retainOwnership)
        {
            if (_exports.Values.Any(id => _devices.DeviceOwner(id) != _owner))
                throw new LocalizedOperationException("Error.DeviceInUse");
        }
        else
            await _devices.AcquireTargetsAsync(_owner, _exports.Values.ToArray(), ct, allowInputs: true);
        _devices.RawGatt.GattNotificationReceived += OnGattNotification;
        _clock.Restart();
        _running = true;
        _loop = Run();
    }
    private RemoteDevice Describe(string exported)
    {
        if (!_exports.TryGetValue(exported, out var local) || !_devices.Connections.TryGetValue(local, out var connection))
            throw new RemoteRpcException(-32004, "Error.NotConnected");
        var device = connection.Device;
        return new(exported, device.Name, device.Kind, device.ProtocolId!, device.ChannelMask ?? (1 << device.ChannelCount) - 1,
            device.Profile!.Capabilities, device.Battery, device.Signal, device.Simulated == true);
    }
    internal async Task<object?> Dispatch(RemotePeerSession peer, string method, JsonElement parameters, CancellationToken ct)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(5));
        await _gate.WaitAsync(operation.Token);
        try
        {
            if (!_running)
                throw new RemoteRpcException(-32000, "Error.RemoteStopped");
            if (method == "system.hello")
                return new RemoteHello(RemoteProtocol.Version, Id, Settings.LeaseMilliseconds, RemoteProtocol.MaximumMessageBytes, _clock.ElapsedMilliseconds);
            if (method == "devices.list")
                return _exports.Keys.Select(Describe).ToArray();
            if (method == "devices.describe")
                return Describe(RemoteProtocol.Parameter<string>(parameters, "deviceId"));
            if (method == "lease.acquire")
            {
                if (_paused)
                    throw new RemoteRpcException(-32000, "Error.RemoteStopped");
                return await Acquire(peer, parameters, operation.Token);
            }
            var leaseId = RemoteProtocol.Parameter<Guid>(parameters, "lease");
            if (!_leases.TryGetValue(leaseId, out var lease) || lease.Peer != peer || lease.Faulted || lease.AgeMilliseconds > Settings.LeaseMilliseconds)
                throw new RemoteRpcException(-32003, "Error.RemoteLease");
            if (method == "lease.renew")
            {
                lease.Renew();
                return new RemoteHeartbeat(_clock.ElapsedMilliseconds);
            }
            if (method == "lease.release")
            {
                await CloseLease(lease, operation.Token);
                return true;
            }
            var sequence = RemoteProtocol.Parameter<long>(parameters, "sequence");
            if (sequence <= lease.Sequence)
                throw new RemoteRpcException(-32003, "Error.RemoteSequence");
            var deadline = RemoteProtocol.Parameter<long>(parameters, "deadline");
            if (deadline < _clock.ElapsedMilliseconds || deadline > _clock.ElapsedMilliseconds + 1000)
                throw new RemoteRpcException(-32003, "Error.RemoteExpired");
            lease.Sequence = sequence;
            lease.Renew();
            try
            {
                if (method.StartsWith("gatt.", StringComparison.Ordinal))
                {
                    if (lease.Type != RemoteLeaseType.Gatt)
                        throw new RemoteRpcException(-32003, "Error.RemoteLeaseType");
                    return await lease.Gatt!.ExecuteAsync(method, parameters, operation.Token);
                }
                if (!method.StartsWith("control.", StringComparison.Ordinal) || lease.Type != RemoteLeaseType.Control)
                    throw new RemoteRpcException(-32601, "Error.RemoteMethod");
                return await lease.Control.Execute(method, parameters, operation.Token);
            }
            catch (Exception error) when (error is IOException or AggregateException or OperationCanceledException)
            {
                try
                {
                    await CloseLease(lease, CancellationToken.None);
                }
                catch (Exception cleanup) { throw new LocalizedAggregateException("Error.StopUnconfirmed", [error, cleanup]); }
                throw;
            }
        }
        finally { _gate.Release(); }
    }
    private async Task<RemoteLease> Acquire(RemotePeerSession peer, JsonElement parameters, CancellationToken ct)
    {
        var ids = RemoteProtocol.Parameter<string[]>(parameters, "deviceIds");
        var type = RemoteProtocol.Parameter<RemoteLeaseType>(parameters, "type");
        if (_allowedLease is { } allowed && type != allowed)
            throw new RemoteRpcException(-32003, "Error.RemoteLeaseType");
        if (!Enum.IsDefined(type) || ids.Length is < 1 or > 32 || ids.Distinct().Count() != ids.Length || ids.Any(id => !_exports.ContainsKey(id)))
            throw new RemoteRpcException(-32602, "Error.RemoteRequest");
        if (_leases.Values.Any(lease => lease.PublicIds.Intersect(ids).Any()))
            throw new RemoteRpcException(-32002, "Error.DeviceInUse");
        var lease = new RemoteDeviceLease(peer, type, ids, ids.Select(id => _exports[id]).ToArray(), _devices, _owner, Settings.ControlLimit,
            () => _clock.ElapsedMilliseconds, Describe, _controlDevices);
        _leases.Add(lease.Id, lease);
        try
        {
            if (lease.Gatt is { } gatt)
                await gatt.OpenAsync(ct);
            Volatile.Write(ref _notificationLeases, _leases.Values.ToArray());
            return new(lease.Id, type, ids, Settings.LeaseMilliseconds);
        }
        catch (Exception error)
        {
            try
            {
                await CloseLease(lease, CancellationToken.None);
            }
            catch (Exception cleanup) { throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]); }
            throw;
        }
    }
    private void OnGattNotification(GattDeviceNotification notification)
    {
        // Notifications may be synchronous inside a GATT write, so never acquire the operation gate here.
        foreach (var lease in Volatile.Read(ref _notificationLeases))
            lease.Gatt?.OnNotification(notification);
    }
    private RemoteDeviceLease[] _notificationLeases = [];
    private async Task CloseLease(RemoteDeviceLease lease, CancellationToken ct)
    {
        lease.Faulted = true;
        List<Exception> errors = [];
        if (lease.Gatt is { } gatt)
        {
            try
            {
                await gatt.CloseAsync(ct);
            }
            catch (Exception error) { errors.Add(error); }
        }
        try
        {
            await lease.Control.Stop(ct);
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
        _leases.Remove(lease.Id);
        Volatile.Write(ref _notificationLeases, _leases.Values.ToArray());
        lease.Peer.Peer.Notify("lease.revoked", new
        {
            lease = lease.Id
        });
    }
    internal async Task Disconnect(RemotePeerSession peer)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var lease in _leases.Values.Where(lease => lease.Peer == peer).ToArray())
            {
                try
                {
                    await CloseLease(lease, CancellationToken.None);
                }
                catch (Exception error) { Faulted?.Invoke(error); }
            }
        }
        finally { _gate.Release(); }
    }
    private async Task Run()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                if (Remaining == TimeSpan.Zero)
                {
                    await StopAsync();
                    return;
                }
                await _gate.WaitAsync(_lifetime.Token);
                try
                {
                    foreach (var lease in _leases.Values.ToArray())
                    {
                        if (lease.Faulted)
                            continue;
                        try
                        {
                            if (lease.AgeMilliseconds > Settings.LeaseMilliseconds)
                                await CloseLease(lease, _lifetime.Token);
                            else
                                await lease.Control.Tick();
                        }
                        catch (Exception error)
                        {
                            lease.Faulted = true;
                            try
                            {
                                await CloseLease(lease, CancellationToken.None);
                            }
                            catch (Exception cleanup) { error = new LocalizedAggregateException("Error.Cleanup", [error, cleanup]); }
                            Faulted?.Invoke(error);
                        }
                    }
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Faulted?.Invoke(error); }
    }
    public async Task StopAsync()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException) { } // A failed physical stop can be retried after the listener has closed.
        await _gate.WaitAsync();
        try
        {
            _running = false;
            _clock.Stop();
            List<Exception> errors = [];
            foreach (var lease in _leases.Values.ToArray())
            {
                try
                {
                    await CloseLease(lease, CancellationToken.None);
                }
                catch (Exception error) { errors.Add(error); }
            }
            try
            {
                await _devices.StopOwnedAsync(_owner, scope: _exports.Values.ToArray(), release: !_retainOwnership);
            }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0)
                throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await StopAsync();
        }
        finally
        {
            _devices.RawGatt.GattNotificationReceived -= OnGattNotification;
            await _loop;
            _lifetime.Dispose();
        }
    }
    public async Task SetPausedAsync(bool paused)
    {
        await _gate.WaitAsync();
        try
        {
            _paused = paused;
            if (paused)
                foreach (var lease in _leases.Values.ToArray())
                    await CloseLease(lease, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }
}
