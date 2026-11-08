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
    public Guid Id { get; }
    public RemoteServerSettings Settings { get; }
    public bool IsRunning => _running;
    public Task Completion => _loop;
    public TimeSpan Elapsed => _clock.Elapsed;
    public TimeSpan? Duration => _duration;
    public TimeSpan? Remaining => _duration is { } duration ? TimeSpan.FromSeconds(Math.Max(0, (duration - Elapsed).TotalSeconds)) : null;
    public event Action<Exception>? Faulted;
    internal void ReportFault(Exception error) => Faulted?.Invoke(error);
    public RemoteExportSession(Guid id, DeviceController devices, IReadOnlyList<string> targets, RemoteServerSettings settings, TimeSpan? duration = null)
    {
        settings.Validate();
        if (id == Guid.Empty || targets.Count > 32 || duration is { } limit && (limit.TotalSeconds < 1 || limit.TotalSeconds > 7200))
            throw new LocalizedArgumentException("Error.RemoteSettings");
        Id = id;
        _devices = devices;
        Settings = settings;
        _duration = duration;
        _exports = targets.Distinct().ToDictionary(LocalExportId, value => value);
    }
    private string LocalExportId(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Id:N}/{id}")))[..24].ToLowerInvariant();
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new LocalizedOperationException("Error.AlreadyRunning");
        if (_exports.Count == 0 || _exports.Values.Any(id => !_devices.Connections.TryGetValue(id, out var connection) ||
            connection.Device.Origin != DeviceOrigin.Local || connection.Device.Profile is not { Supported: true } || connection.Device.Simulated == true))
            throw new LocalizedOperationException("Error.RemoteLocalDevices");
        await _devices.AcquireTargetsAsync(Id, _exports.Values.ToArray(), ct, allowInputs: true);
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
            if (!_running) throw new RemoteRpcException(-32000, "Error.RemoteStopped");
            if (method == "system.hello") return new RemoteHello(1, Id, Settings.LeaseMilliseconds, RemoteProtocol.MaximumMessageBytes, _clock.ElapsedMilliseconds);
            if (method == "devices.list") return _exports.Keys.Select(Describe).ToArray();
            if (method == "devices.describe") return Describe(RemoteProtocol.Parameter<string>(parameters, "deviceId"));
            if (method == "lease.acquire") return await Acquire(peer, parameters, operation.Token);
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
            if (sequence <= lease.Sequence) throw new RemoteRpcException(-32003, "Error.RemoteSequence");
            var deadline = RemoteProtocol.Parameter<long>(parameters, "deadline");
            if (deadline < _clock.ElapsedMilliseconds || deadline > _clock.ElapsedMilliseconds + 1000) throw new RemoteRpcException(-32003, "Error.RemoteExpired");
            lease.Sequence = sequence;
            lease.Renew();
            try
            {
                if (method.StartsWith("gatt.", StringComparison.Ordinal))
                {
                    if (lease.Type != RemoteLeaseType.Gatt) throw new RemoteRpcException(-32003, "Error.RemoteLeaseType");
                    return await DispatchGatt(lease, method, parameters, operation.Token);
                }
                if (!method.StartsWith("control.", StringComparison.Ordinal) || lease.Type != RemoteLeaseType.Control)
                    throw new RemoteRpcException(-32601, "Error.RemoteMethod");
                return await lease.Control.Execute(method, parameters, operation.Token);
            }
            catch (Exception error) when (error is IOException or AggregateException or OperationCanceledException)
            {
                try { await CloseLease(lease, CancellationToken.None); }
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
        if (!Enum.IsDefined(type) || ids.Length is < 1 or > 32 || ids.Distinct().Count() != ids.Length || ids.Any(id => !_exports.ContainsKey(id)))
            throw new RemoteRpcException(-32602, "Error.RemoteRequest");
        if (_leases.Values.Any(lease => lease.PublicIds.Intersect(ids).Any())) throw new RemoteRpcException(-32002, "Error.DeviceInUse");
        var lease = new RemoteDeviceLease(peer, type, ids, ids.Select(id => _exports[id]).ToArray(), _devices, Id, Settings.ControlLimit);
        _leases.Add(lease.Id, lease);
        try
        {
            if (type == RemoteLeaseType.Gatt)
                await RegisterDescriptions(lease, ct);
            return new(lease.Id, type, ids, Settings.LeaseMilliseconds);
        }
        catch (Exception error)
        {
            try { await CloseLease(lease, CancellationToken.None); }
            catch (Exception cleanup) { throw new LocalizedAggregateException("Error.Cleanup", [error, cleanup]); }
            throw;
        }
    }
    private string Local(RemoteDeviceLease lease, JsonElement parameters)
    {
        var id = RemoteProtocol.Parameter<string>(parameters, "deviceId");
        if (!lease.PublicIds.Contains(id)) throw new RemoteRpcException(-32003, "Error.RemoteLease");
        return _exports[id];
    }
    private async Task<object?> DispatchGatt(RemoteDeviceLease lease, string method, JsonElement parameters, CancellationToken ct)
    {
        var local = Local(lease, parameters);
        switch (method)
        {
            case "gatt.describe":
                return new RemoteGattDescription(Describe(LocalExportId(local)), lease.Descriptions[local].Attributes.ToArray(), lease.Descriptions[local].MaximumWriteLength);
            case "gatt.read":
                return await _devices.RawGatt.ReadGattAsync(local, lease.Id, RemoteProtocol.Parameter<GattAddress>(parameters, "address"), ct);
            case "gatt.write":
                await _devices.RawGatt.WriteGattAsync(local, lease.Id, [RemoteProtocol.Parameter<GattWrite>(parameters, "write")], ct);
                return true;
            case "gatt.batch":
                await _devices.RawGatt.WriteGattAsync(local, lease.Id, RemoteProtocol.Parameter<GattWrite[]>(parameters, "writes"), ct);
                return true;
            case "gatt.subscribe":
            case "gatt.unsubscribe":
                var address = RemoteProtocol.Parameter<GattAddress>(parameters, "address");
                if (!lease.Descriptions[local].Attributes.Any(attribute => attribute.Notify && attribute.Address == address)) throw new RemoteRpcException(-32602, "Error.DeviceFeatures");
                if (method == "gatt.subscribe") lease.Subscriptions.TryAdd((local, address), 0);
                else lease.Subscriptions.TryRemove((local, address), out _);
                return true;
            default: throw new RemoteRpcException(-32601, "Error.RemoteMethod");
        }
    }
    private void OnGattNotification(GattDeviceNotification notification)
    {
        // Notifications may be synchronous inside a GATT write, so never acquire the operation gate here.
        foreach (var lease in Volatile.Read(ref _notificationLeases))
            if (lease.Subscriptions.ContainsKey((notification.DeviceId, notification.Notification.Address)))
                lease.Peer.Peer.Notify("gatt.notification", new RemoteGattNotification(lease.Id, LocalExportId(notification.DeviceId), notification.Notification, _clock.ElapsedMilliseconds));
    }
    private RemoteDeviceLease[] _notificationLeases = [];
    private async Task CloseLease(RemoteDeviceLease lease, CancellationToken ct)
    {
        lease.Faulted = true;
        List<Exception> errors = [];
        if (lease.Type == RemoteLeaseType.Gatt)
        {
            foreach (var local in lease.Descriptions.Keys)
            {
                if (!_devices.Connections.ContainsKey(local)) continue;
                try { await _devices.RawGatt.CloseGattAsync(local, lease.Id, ct); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        try { await lease.Control.Stop(ct); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
        _leases.Remove(lease.Id);
        Volatile.Write(ref _notificationLeases, _leases.Values.ToArray());
        lease.Peer.Peer.Notify("lease.revoked", new { lease = lease.Id });
    }
    internal async Task Disconnect(RemotePeerSession peer)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var lease in _leases.Values.Where(lease => lease.Peer == peer).ToArray())
            {
                try { await CloseLease(lease, CancellationToken.None); }
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
                        if (lease.Faulted) continue;
                        try
                        {
                            if (lease.AgeMilliseconds > Settings.LeaseMilliseconds) await CloseLease(lease, _lifetime.Token);
                            else await lease.Control.Tick();
                        }
                        catch (Exception error)
                        {
                            lease.Faulted = true;
                            try { await CloseLease(lease, CancellationToken.None); }
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
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try
        {
            _running = false;
            _clock.Stop();
            List<Exception> errors = [];
            foreach (var lease in _leases.Values.ToArray())
            {
                try { await CloseLease(lease, CancellationToken.None); }
                catch (Exception error) { errors.Add(error); }
            }
            try { await _devices.StopOwnedAsync(Id); }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count > 0) throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAsync(); }
        finally
        {
            _devices.RawGatt.GattNotificationReceived -= OnGattNotification;
            await _loop;
            _lifetime.Dispose();
        }
    }
    internal async Task RegisterDescriptions(RemoteDeviceLease lease, CancellationToken ct)
    {
        foreach (var id in lease.LocalIds)
            lease.Descriptions[id] = await _devices.RawGatt.OpenGattAsync(id, lease.Id, ct);
        Volatile.Write(ref _notificationLeases, _leases.Values.ToArray());
    }
}
