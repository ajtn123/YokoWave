using YokoWave.Core.Localization;
namespace YokoWave.Core;

public sealed class DeviceConnection(DeviceDescriptor device, double limit)
{
    public DeviceDescriptor Device { get; } = device;
    public double Limit { get; internal set; } = limit;
    public OutputFrame Output { get; internal set; } = OutputFrame.Zero;
}

/// <summary>Single serialized gateway to all outputs, including stop and disconnect.</summary>
public sealed class DeviceController(IDeviceTransport transport) : IAsyncDisposable
{
    private readonly Dictionary<string, DeviceConnection> _connections = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IReadOnlyDictionary<string, DeviceConnection> Connections => _connections;
    public string TransportName => transport.DisplayName;
    public event EventHandler? Changed;

    public Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken ct = default) => transport.ScanAsync(ct);

    public async Task ConnectAsync(DeviceDescriptor device, double limit = 60, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connections.ContainsKey(device.Id)) return;
            await transport.ConnectAsync(device, ct);
            try { await transport.SendAsync(device.Id, OutputFrame.Zero, ct); }
            catch { await transport.DisconnectAsync(device.Id, CancellationToken.None); throw; }
            _connections.Add(device.Id, new(device, Clamp(limit)));
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetLimitAsync(string id, double limit, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var connection = Get(id);
            connection.Limit = Clamp(limit);
            await WriteCoreAsync(connection, connection.Output, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SendAsync(string id, OutputFrame frame, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await WriteCoreAsync(Get(id), frame, ct); }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteCoreAsync(DeviceConnection connection, OutputFrame frame, CancellationToken ct)
    {
        var output = frame with
        {
            ChannelA = Math.Min(Clamp(frame.ChannelA), connection.Limit),
            ChannelB = connection.Device.Kind == DeviceKind.DualChannel ? Math.Min(Clamp(frame.ChannelB), connection.Limit) : 0,
            Frequency = double.IsFinite(frame.Frequency) ? Math.Clamp(frame.Frequency, 1, 100) : 1
        };
        await transport.SendAsync(connection.Device.Id, output, ct);
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
                await transport.DisconnectAsync(id, CancellationToken.None);
                _connections.Remove(id);
            }
        }
        finally { _gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private DeviceConnection Get(string id) => _connections.TryGetValue(id, out var item)
        ? item : throw new LocalizedOperationException("Error.NotConnected");
    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    public async ValueTask DisposeAsync()
    {
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
            try { await transport.DisposeAsync(); }
            catch (Exception e) { errors.Add(e); }
            _gate.Dispose();
        }
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.Cleanup", errors);
    }
}
