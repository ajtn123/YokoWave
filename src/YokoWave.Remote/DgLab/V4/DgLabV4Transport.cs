using System.Collections.Concurrent;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>App slots participate in the ordinary device router. Short tasks bound output if a controller disappears.</summary>
public sealed class DgLabV4Transport : IDeviceTransport, IDeviceStatusSource, IDeviceCommandTransport
{
    private readonly DgLabV4Peer _peer = new();
    private readonly DgLabV4DeviceCatalog _catalog;
    private readonly Uri _url;
    private readonly Uri _advertised;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, byte> _connected = new();
    private readonly ConcurrentDictionary<long, Task> _tasks = new();
    private readonly ConcurrentDictionary<string, DeviceDescriptor> _discovered = new();
    private long _taskId;
    private int _disposed;
    public string DisplayName => L10n.Get("DgLab.Title");
    public bool IsSimulation => false;
    public string PairingUrl => !IsConnected ? "" : DgLabV4Protocol.PairingUrl(_advertised.ToString(), _peer.ClientId);
    public string ControllerId => IsConnected ? _peer.ClientId : "";
    public Exception? Error { get; private set; }
    public bool IsConnected => _peer.IsConnected;
    public IReadOnlyList<DgLabV4Slot> Slots => _catalog.Slots;
    public event EventHandler<DeviceStatusUpdate>? StatusChanged;
    public event EventHandler<DeviceTransportFault>? Faulted;
    public event Action? DevicesChanged;

    public DgLabV4Transport(Guid endpoint, string url, TimeProvider? clock = null, string? advertisedHost = null)
    {
        _url = DgLabV4Protocol.ControllerUri(url);
        _advertised = advertisedHost is { Length: > 0 } ? new UriBuilder(_url) { Host = advertisedHost }.Uri : _url;
        _clock = clock ?? TimeProvider.System;
        _catalog = new(endpoint, _clock);
        _catalog.Changed += CatalogChanged;
        _peer.FrameReceived += Receive;
        _peer.Disconnected += error =>
        {
            foreach (string id in _connected.Keys)
                Faulted?.Invoke(this, new(id, true, error ?? new LocalizedIOException("Error.RemoteDisconnected")));
            DevicesChanged?.Invoke();
        };
    }

    public async Task<IReadOnlyList<DeviceDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!_peer.IsConnected)
            await _peer.ConnectAsync(_url, cancellationToken).ConfigureAwait(false);
        return _catalog.Slots.Select(slot => slot.Descriptor).ToArray();
    }

    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken cancellationToken = default)
    {
        var slot = Require(device.Id);
        if (!slot.Online || !device.IsSupported)
            throw new LocalizedOperationException("Error.NotConnected");
        await ClearAsync(slot, cancellationToken).ConfigureAwait(false);
        _connected[device.Id] = 0;
        Publish(slot);
    }

    public async Task SendAsync(string deviceId, OutputFrame frame, CancellationToken cancellationToken = default)
    {
        frame.Validate();
        var slot = Require(deviceId);
        if (!_connected.ContainsKey(deviceId) || !slot.Online)
            throw new LocalizedOperationException("Error.NotConnected");
        if (slot.Descriptor.IsInput)
        {
            if (frame.ChannelA > 0 || frame.ChannelB > 0 || frame.ChannelC > 0)
                throw new LocalizedOperationException("Error.SensorOnly");
            return;
        }
        if (frame.NativeProgram is not null || frame.Pump is not null || frame.ChannelC > 0 || frame.DgBalances is not null ||
            frame.Hardware is { RelativeStrength: true } or { AuxiliaryMotor: not AuxiliaryMotorMode.Off })
        {
            throw new LocalizedOperationException("Error.ProtocolSettings");
        }
        if (frame.ChannelA == 0 && frame.ChannelB == 0)
        {
            await ClearAsync(slot, cancellationToken).ConfigureAwait(false);
            return;
        }
        for (int channel = 0; channel < 2; channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double percent = WaveformRendering.Strength(frame, channel, window: true);
            if (frame.Gentle)
                percent *= .65;
            double value = Math.Min(Math.Floor(percent * slot.Maximum / 100), slot.Limit(channel));
            // Accepted tasks outlive this frame's cancellation; stop is an ordered clear/reset request.
            Track(deviceId, _peer.RequestAsync(slot.AppId, "device.op", new { s = slot.Device.SlotId, t = 4, c = channel, p = 1, d = 400, im = true, v = value }));
            string encoded = DgLabV4Pulse.Encode(frame, channel);
            Track(deviceId, _peer.RequestAsync(slot.AppId, "device.op", new { s = slot.Device.SlotId, t = 0, c = channel, p = 1, d = 100, im = true, ver = 3, v = new[] { encoded } }));
        }
    }

    public async Task ExecuteCommandAsync(string deviceId, DeviceCommand command, CancellationToken cancellationToken = default)
    {
        if (command is not QueryDevice)
            throw new LocalizedOperationException("Error.DeviceFeatures");
        var slot = Require(deviceId);
        var result = await _peer.RequestAsync(slot.AppId, "devices.get", cancellationToken: cancellationToken).ConfigureAwait(false);
        _catalog.Apply(slot.AppId, result, descriptions: true);
        Publish(Require(deviceId));
    }

    public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (!_connected.ContainsKey(deviceId))
            return;
        var slot = _catalog.Find(deviceId);
        if (slot is not null && _peer.IsConnected)
            await ClearAsync(slot, cancellationToken).ConfigureAwait(false);
        _connected.TryRemove(deviceId, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        List<Exception> errors = [];
        foreach (string id in _connected.Keys)
        {
            try
            {
                await DisconnectAsync(id).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        await _peer.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(_tasks.Values).ConfigureAwait(false);
        if (errors.Count > 0)
            throw new LocalizedAggregateException("Error.Cleanup", errors);
    }

    private DgLabV4Slot Require(string id) => _catalog.Find(id) ?? throw new LocalizedOperationException("Error.NotConnected");

    private async Task ClearAsync(DgLabV4Slot slot, CancellationToken cancellationToken)
    {
        await _peer.RequestAsync(slot.AppId, "device.op.clear", new { s = slot.Device.SlotId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (slot.Descriptor.IsInput)
            return;
        for (int channel = 0; channel < 2; channel++)
            await _peer.RequestAsync(slot.AppId, "device.op", new { s = slot.Device.SlotId, t = 7, c = channel, v = 0, im = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void Receive(string type, JsonElement frame)
    {
        if (type == "client_attached")
        {
            string id = DgLabV4Protocol.String(frame, "clientId");
            Track("", RefreshAsync(id));
        }
        else if (type == "client_disconnected")
        {
            _catalog.RemoveApp(DgLabV4Protocol.String(frame, "clientId"));
        }
        else if (type == "error" && frame.TryGetProperty("clientId", out var failed) && failed.ValueKind == JsonValueKind.String)
        {
            _catalog.RemoveApp(failed.GetString()!);
        }
        else if (type == "message")
        {
            var data = DgLabV4Protocol.Data(frame);
            if (data.TryGetProperty("t", out var kind) && kind.GetString() == "ev" && data.TryGetProperty("ev", out var action))
            {
                string app = DgLabV4Protocol.String(frame, "clientId");
                if (action.GetString() is "devices.snapshot" or "devices.patch" or "slots.patch")
                {
                    _catalog.Apply(app, data);
                }
                else if (action.GetString() == "custom.action" && data.TryGetProperty("action", out var number) && number.TryGetInt32(out int value) && value is >= 0 and <= 9)
                {
                    foreach (var slot in _catalog.Slots.Where(slot => slot.AppId == app && _connected.ContainsKey(slot.Id)))
                        StatusChanged?.Invoke(this, new(slot.Id, telemetry: new(EventId: value, EventActive: true, EventCapturedAt: _clock.GetUtcNow())));
                }
            }
        }
    }

    private async Task RefreshAsync(string app)
    {
        var result = await _peer.RequestAsync(app, "devices.get").ConfigureAwait(false);
        _catalog.Apply(app, result, descriptions: true);
        Error = null;
    }

    private void CatalogChanged(IReadOnlyList<DgLabV4Slot> changed, IReadOnlyList<string> removed)
    {
        bool structural = removed.Count > 0;
        foreach (string id in removed)
        {
            _discovered.TryRemove(id, out _);
            if (_connected.TryRemove(id, out _))
                Faulted?.Invoke(this, new(id, true, new LocalizedIOException("Error.RemoteDisconnected")));
        }
        foreach (var slot in changed)
        {
            var descriptor = slot.Descriptor;
            structural |= !_discovered.TryGetValue(slot.Id, out var previous) || previous.Name != descriptor.Name || previous.Kind != descriptor.Kind;
            _discovered[slot.Id] = descriptor;
        }
        foreach (var slot in changed.Where(slot => _connected.ContainsKey(slot.Id)))
        {
            if (!slot.Online)
                Faulted?.Invoke(this, new(slot.Id, true, new LocalizedIOException("Error.RemoteDisconnected")));
            else
                Publish(slot);
        }
        if (structural)
            DevicesChanged?.Invoke();
    }

    private void Publish(DgLabV4Slot slot)
    {
        var props = slot.Device.Props;
        var telemetry = slot.Descriptor.IsInput ? new DeviceTelemetry(PressureKpa: DgLabV4Slot.Number(props?["pressure"]), PressureCapturedAt: slot.CapturedAt)
            : new DeviceTelemetry(IntensityA: (int)DgLabV4Slot.Number(props?["intensityA"]), IntensityB: (int)DgLabV4Slot.Number(props?["intensityB"]));
        StatusChanged?.Invoke(this, new(slot.Id, slot.Descriptor.Battery, telemetry: telemetry, telemetryAge: _clock.GetUtcNow() - slot.CapturedAt));
    }

    private void Track(string id, Task task)
    {
        long key = Interlocked.Increment(ref _taskId);
        var observed = ObserveAsync(key, id, task);
        _tasks[key] = observed;
        if (observed.IsCompleted)
            _tasks.TryRemove(key, out _);
    }

    private async Task ObserveAsync(long key, string id, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (_disposed == 0 && id.Length > 0)
            {
                Faulted?.Invoke(this, new(id, false, error));
            }
            else if (_disposed == 0)
            {
                Error = error;
                DevicesChanged?.Invoke();
            }
        }
        finally
        {
            _tasks.TryRemove(key, out _);
        }
    }
}
