using System.Diagnostics;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Remote;

/// <summary>High-level operations use the same engine and command validation as local playback.</summary>
internal sealed class RemoteControlSession(DeviceController devices, Guid owner, IReadOnlyDictionary<string, string> targets, double maximum, Action<RemoteStatus> notify)
{
    private SessionEngine? _engine;
    private readonly Stopwatch _clock = new();
    private OutputFrame? _manual;
    private bool _paused;
    private readonly HashSet<string> _subscriptions = [];
    private readonly Dictionary<string, RemoteStatus> _lastStatus = [];
    private RemoteStatus Status(string exported)
    {
        if (!targets.TryGetValue(exported, out var id) || !devices.Connections.TryGetValue(id, out var connection))
            throw new RemoteRpcException(-32004, "Error.NotConnected");
        return new(exported, connection.Telemetry, connection.Device.Battery, connection.Motion, connection.ContactA, connection.ContactB, connection.LightEnabled);
    }
    private string Target(JsonElement parameters)
    {
        var id = RemoteProtocol.Parameter<string>(parameters, "deviceId");
        return targets.TryGetValue(id, out var local) ? local : throw new RemoteRpcException(-32003, "Error.RemoteLease");
    }
    public async Task<object?> Execute(string method, JsonElement parameters, CancellationToken ct)
    {
        switch (method)
        {
            case "control.status":
                return Status(RemoteProtocol.Parameter<string>(parameters, "deviceId"));
            case "control.subscribe":
            case "control.unsubscribe":
                _ = Target(parameters);
                var subscription = RemoteProtocol.Parameter<string>(parameters, "deviceId");
                if (method == "control.subscribe") _subscriptions.Add(subscription);
                else _subscriptions.Remove(subscription);
                _lastStatus.Remove(subscription);
                return true;
            case "control.command":
                await devices.ExecuteCommandAsync(Target(parameters), RemoteProtocol.Parameter<DeviceCommand>(parameters, "command"), owner, ct);
                return true;
            case "control.play":
            case "control.playlist":
            case "control.native":
            case "control.pump":
                var request = RemoteProtocol.Parameter<RemoteControlProgram>(parameters, "playback");
                if (request.Program.Kind is not (ModeKind.Waveform or ModeKind.NativeProgram or ModeKind.Pump)) throw new RemoteRpcException(-32602, "Error.ModeSettings");
                if (method == "control.native" && request.Program.Kind != ModeKind.NativeProgram ||
                    method == "control.pump" && request.Program.Kind != ModeKind.Pump ||
                    method == "control.playlist" && (request.Program.Kind != ModeKind.Waveform || request.Program.Playlist is not { Count: > 0 }))
                    throw new RemoteRpcException(-32602, "Error.ModeSettings");
                request.Program.Validate();
                await Stop(ct);
                var preset = request.Waveform ?? request.Program.Playlist?.FirstOrDefault() ?? new WaveformPreset(Guid.NewGuid(), L10n.Get("Remote.Control"), WaveShape.Pulse, 1, 20);
                _engine = new(devices, ownerId: owner, retainOwnership: true)
                {
                    ChannelA = Clamp(request.ChannelA),
                    ChannelB = Clamp(request.ChannelB),
                    ChannelC = Clamp(request.ChannelC)
                };
                await _engine.StartAsync(targets.Values, preset, request.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                    request.Program with { OutputLimit = Math.Min(maximum, request.Program.OutputLimit) });
                _clock.Restart();
                await _engine.AdvanceAsync(TimeSpan.Zero);
                return true;
            case "control.output":
                var frame = RemoteProtocol.Parameter<OutputFrame>(parameters, "frame");
                if (!double.IsFinite(frame.OutputLimit) || frame.OutputLimit is < 0 or > 100 || frame.NativeProgram is not null)
                    throw new RemoteRpcException(-32602, "Error.InvalidSettings");
                await Stop(ct);
                _manual = frame with { OutputLimit = Math.Min(maximum, frame.OutputLimit) };
                await SendManual(ct);
                return true;
            case "control.strength":
                var a = Clamp(RemoteProtocol.Parameter<double>(parameters, "channelA"));
                var b = Clamp(RemoteProtocol.Parameter<double>(parameters, "channelB"));
                var c = parameters.TryGetProperty("channelC", out var cv) ? Clamp(cv.GetDouble()) : 0;
                if (_engine is not null)
                {
                    _engine.ChannelA = a;
                    _engine.ChannelB = b;
                    _engine.ChannelC = c;
                }
                else _manual = (_manual ?? new(0, 0, 20, OutputLimit: maximum)) with { ChannelA = a, ChannelB = b, ChannelC = c };
                await Tick();
                return true;
            case "control.waveform":
                if (_engine is null) throw new RemoteRpcException(-32000, "Error.RemoteNotPlaying");
                return await _engine.ChangeWaveformAsync(RemoteProtocol.Parameter<WaveformPreset>(parameters, "waveform"));
            case "control.pause":
                if (_engine is not null) await _engine.SetPausedAsync(true);
                else await devices.StopOwnedAsync(owner, ct, targets.Values.ToArray(), release: false);
                _clock.Stop();
                _paused = true;
                return true;
            case "control.resume":
                if (_engine is not null) await _engine.SetPausedAsync(false);
                _clock.Start();
                _paused = false;
                return true;
            case "control.stop":
                await Stop(ct);
                return true;
            default: throw new RemoteRpcException(-32601, "Error.RemoteMethod");
        }
    }
    private double Clamp(double value) => double.IsFinite(value) && value >= 0 ? Math.Min(value, maximum) : throw new RemoteRpcException(-32602, "Error.InvalidSettings");
    private async Task SendManual(CancellationToken ct)
    {
        if (_paused || _manual is not { } frame) return;
        foreach (var id in targets.Values) await devices.SendOwnedAsync(owner, id, frame, ct);
    }
    public async Task Tick()
    {
        if (_engine?.IsRunning == true && !_engine.IsPaused) await _engine.AdvanceAsync(_clock.Elapsed);
        else if (_engine is null) await SendManual(CancellationToken.None);
        foreach (var exported in _subscriptions)
        {
            var status = Status(exported);
            if (_lastStatus.TryGetValue(exported, out var last) && last == status) continue;
            _lastStatus[exported] = status;
            notify(status);
        }
    }
    public async Task Stop(CancellationToken ct)
    {
        _manual = null;
        _paused = false;
        List<Exception> errors = [];
        if (_engine is not null)
        {
            try { await _engine.StopAsync(); }
            catch (Exception error) { errors.Add(error); }
        }
        try { await devices.StopOwnedAsync(owner, ct, targets.Values.ToArray(), release: false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new LocalizedAggregateException("Error.StopUnconfirmed", errors);
        _engine = null;
        _clock.Reset();
    }
}
