using YokoWave.Core.Localization;
using System.Collections.Concurrent;
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
}

/// <summary>Single serialized gateway to all outputs, including stop and disconnect.</summary>
public sealed class DeviceController : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DeviceConnection> _connections = new();
    private readonly ConcurrentDictionary<string, Guid> _owners = new();
    private IDeviceTransport _transport;
    private bool _disposed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IReadOnlyDictionary<string, DeviceConnection> Connections => _connections;
    public string TransportName => _transport.DisplayName;
    public bool IsSimulation => _transport.IsSimulation;
    public bool IsDeviceBusy(string id) => _owners.ContainsKey(id);
    public Guid? DeviceOwner(string id) => _owners.TryGetValue(id, out var owner) ? owner : null;
    public event EventHandler? Changed;
    public event EventHandler<DeviceTransportFault>? Faulted;

    public DeviceController(IDeviceTransport transport)
    {
        _transport = transport;
        Subscribe(transport, true);
    }

    private void Subscribe(IDeviceTransport transport, bool add)
    {
        if (transport is not IDeviceStatusSource source) return;
        if (add) { source.StatusChanged += OnStatusChanged; source.Faulted += OnFaulted; }
        else { source.StatusChanged -= OnStatusChanged; source.Faulted -= OnFaulted; }
    }

    public async Task ChangeTransportAsync(IDeviceTransport next)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_connections.IsEmpty) throw new LocalizedOperationException("Error.TransportConnected");
            var previous = _transport;
            Subscribe(previous, false);
            _transport = next;
            Subscribe(next, true);
            await previous.DisposeAsync();
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async void OnStatusChanged(object? sender, DeviceStatusUpdate update)
    {
        if (_disposed) return;
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || sender != _transport || !_connections.TryGetValue(update.DeviceId, out var connection)) return;
                if (update.Battery is >= 0 and <= 100) connection.Device = connection.Device with { Battery = update.Battery };
                if (update.ContactA is { } a) connection.ContactA = a;
                if (update.ContactB is { } b) connection.ContactB = b;
                if (update.HardwareVersion is { } hardware) connection.HardwareVersion = hardware;
                if (update.SoftwareVersion is { } software) connection.SoftwareVersion = software;
                if (update.LightEnabled is { } light) connection.LightEnabled = light;
            }
            finally { _gate.Release(); }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (ObjectDisposedException) when (_disposed) { }
    }

    private async void OnFaulted(object? sender, DeviceTransportFault fault)
    {
        if (_disposed) return;
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || sender != _transport || !_connections.ContainsKey(fault.DeviceId)) return;
                if (fault.Disconnected)
                {
                    _connections.TryRemove(fault.DeviceId, out _);
                    _owners.TryRemove(fault.DeviceId, out _);
                    try { await _transport.DisconnectAsync(fault.DeviceId); } catch { /* The link is already lost. */ }
                }
            }
            finally { _gate.Release(); }
            Changed?.Invoke(this, EventArgs.Empty);
            Faulted?.Invoke(this, fault);
        }
        catch (ObjectDisposedException) when (_disposed) { }
    }

    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken ct = default) => _transport.ScanAsync(ct);

    public async Task AcquireTargetsAsync(Guid owner, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (owner == Guid.Empty || ids.Count == 0 || ids.Any(id => !_connections.ContainsKey(id))) throw new LocalizedOperationException("Error.SelectConnected");
            if (ids.Any(id => _owners.TryGetValue(id, out var current) && current != owner)) throw new LocalizedOperationException("Error.DeviceInUse");
            foreach (var id in ids) _owners[id] = owner;
            try { foreach (var id in ids) await WriteCoreAsync(Get(id), OutputFrame.Zero, ct); }
            catch
            {
                foreach (var id in ids)
                {
                    try { await WriteCoreAsync(Get(id), OutputFrame.Zero, CancellationToken.None); _owners.TryRemove(id, out _); }
                    catch { /* Keep the lease until a stop succeeds or the device disconnects. */ }
                }
                throw;
            }
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SendOwnedAsync(Guid owner, string id, OutputFrame frame, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_owners.TryGetValue(id, out var current) || current != owner) throw new LocalizedOperationException("Error.DeviceInUse");
            await WriteCoreAsync(Get(id), frame, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopOwnedAsync(Guid owner, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        List<Exception> errors = [];
        try
        {
            foreach (var id in _owners.Where(pair => pair.Value == owner).Select(pair => pair.Key).ToArray())
            {
                try
                {
                    if (_connections.TryGetValue(id, out var connection)) await WriteCoreAsync(connection, OutputFrame.Zero, ct);
                    _owners.TryRemove(id, out _);
                }
                catch (Exception error) { errors.Add(error); }
            }
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
    }

    public async Task RefreshFeaturesAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var connection = Get(id);
            if (_owners.ContainsKey(id)) throw new LocalizedOperationException("Error.DeviceInUse");
            if (_transport is not IElectricalDeviceFeatures features) throw new LocalizedOperationException("Error.DeviceFeatures");
            var state = await features.RefreshFeaturesAsync(id, ct);
            connection.HardwareVersion = state.HardwareVersion; connection.SoftwareVersion = state.SoftwareVersion;
            connection.LightEnabled = state.LightEnabled;
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetLightAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var connection = Get(id);
            if (_owners.ContainsKey(id)) throw new LocalizedOperationException("Error.DeviceInUse");
            if (connection.HardwareVersion is not > 0 || connection.SoftwareVersion is not > 0 || _transport is not IElectricalDeviceFeatures features)
                throw new LocalizedOperationException("Error.DeviceFeatures");
            connection.LightEnabled = await features.SetLightAsync(id, enabled, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connections.ContainsKey(device.Id)) return;
            await _transport.ConnectAsync(device, ct);
            try { await _transport.SendAsync(device.Id, OutputFrame.Zero, ct); }
            catch { await _transport.DisconnectAsync(device.Id, CancellationToken.None); throw; }
            _connections.TryAdd(device.Id, new(device));
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SendAsync(string id, OutputFrame frame, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_owners.ContainsKey(id)) throw new LocalizedOperationException("Error.DeviceInUse");
            await WriteCoreAsync(Get(id), frame, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteCoreAsync(DeviceConnection connection, OutputFrame frame, CancellationToken ct)
    {
        var output = frame with
        {
            ChannelA = Clamp(frame.ChannelA),
            ChannelB = connection.Device.Kind == DeviceKind.DualChannel ? Clamp(frame.ChannelB) : 0,
            Frequency = double.IsFinite(frame.Frequency) ? Math.Clamp(frame.Frequency, 1, 100) : 1
        };
        await _transport.SendAsync(connection.Device.Id, output, ct);
        connection.Output = output;
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        List<Exception> errors = [];
        try
        {
            foreach (var connection in _connections.Values)
            {
                try { await WriteCoreAsync(connection, OutputFrame.Zero, ct); }
                catch (Exception e) { errors.Add(e); }
            }
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
    }

    public async Task DisconnectAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_connections.TryGetValue(id, out var connection)) return;
            try { await WriteCoreAsync(connection, OutputFrame.Zero, ct); }
            finally
            {
                try { await _transport.DisconnectAsync(id, CancellationToken.None); }
                finally { _connections.TryRemove(id, out _); _owners.TryRemove(id, out _); }
            }
        }
        finally { _gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private DeviceConnection Get(string id) => _connections.TryGetValue(id, out var item)
        ? item : throw new LocalizedOperationException("Error.NotConnected");
    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Subscribe(_transport, false);
        List<Exception> errors = [];
        try { await StopAllAsync(); }
        catch (Exception e) { errors.Add(e); }
        finally
        {
            foreach (var id in _connections.Keys.ToArray())
            {
                try { await DisconnectAsync(id); }
                catch (Exception e) { errors.Add(e); }
            }
            try { await _transport.DisposeAsync(); }
            catch (Exception e) { errors.Add(e); }
            _gate.Dispose();
        }
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
