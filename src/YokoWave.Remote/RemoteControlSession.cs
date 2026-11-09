using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;

namespace YokoWave.Remote;

/// <summary>RPC translates requests to ordinary compiled graphs, including manual output.</summary>
internal sealed class RemoteControlSession(DeviceController devices, Guid owner, IReadOnlyDictionary<string, string> targets,
    double maximum, Action<RemoteStatus> notify, Func<long> clock, DeviceController? statusDevices = null)
{
    private PipelineSession? _session;
    private RemotePipelineProgram? _program;
    private OutputFrame _frame = OutputFrame.Zero;
    private readonly HashSet<string> _subscriptions = [];
    private readonly Dictionary<string, RemoteStatus> _lastStatus = [];
    private readonly Dictionary<(string Device, bool Motion), (DateTimeOffset Captured, long Clock)> _captureTimes = [];
    private DeviceDescriptor[] Devices => targets.Values.Select(id => devices.Connections[id].Device).ToArray();
    private RemoteStatus Status(string exported)
    {
        if (!targets.TryGetValue(exported, out var id) || !(statusDevices ?? devices).Connections.TryGetValue(id, out var connection))
            throw new RemoteRpcException(-32004, "Error.NotConnected");
        return new(exported, connection.Telemetry, connection.Device.Battery, connection.Motion, connection.ContactA, connection.ContactB, connection.LightEnabled,
            connection.HardwareVersion, connection.SoftwareVersion,
            CaptureClock(exported, false, connection.TelemetryCapturedAt), CaptureClock(exported, true, connection.Motion?.CapturedAt));
    }
    private long? CaptureClock(string device, bool motion, DateTimeOffset? capturedAt)
    {
        if (capturedAt is not { } captured)
            return null;
        var key = (device, motion);
        if (_captureTimes.TryGetValue(key, out var previous) && previous.Captured == captured)
            return previous.Clock;
        var age = (statusDevices ?? devices).Clock.GetUtcNow() - captured;
        var timestamp = clock() - (long)age.TotalMilliseconds;
        _captureTimes[key] = (captured, timestamp);
        return timestamp;
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
                if (method == "control.subscribe")
                    _subscriptions.Add(subscription);
                else
                    _subscriptions.Remove(subscription);
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
                if (method == "control.native" && request.Program.Kind != RemotePlaybackKind.NativeProgram ||
                    method == "control.pump" && request.Program.Kind != RemotePlaybackKind.Pump ||
                    method == "control.playlist" && request.Program.Playlist is not { Count: > 0 })
                    throw new RemoteRpcException(-32602, "Error.PlaybackSettings");
                var program = RemotePipelineProgram.Playback(Devices, request, maximum);
                var playbackOutputs = PrepareOutputs(request.Program.Gentle, request.Program.Hardware, request.Program.Balances);
                await Start(program, ct, playbackOutputs);
                return true;
            case "control.output":
                var frame = RemoteProtocol.Parameter<OutputFrame>(parameters, "frame");
                ValidateFrame(frame);
                var frameOutputs = PrepareOutputs(frame.Gentle, frame.Hardware, frame.DgBalances);
                if (Devices.All(device => device.IsInput))
                {
                    await Stop(ct);
                    return true;
                }
                _frame = frame with
                {
                    OutputLimit = Math.Min(maximum, frame.OutputLimit)
                };
                if (_session is { State: PipelineState.Running } && _program?.FrameSource is { } frameSource)
                {
                    await ConfigureOutputs(frameOutputs, ct);
                    foreach (var output in _program.Graph.Nodes.Where(n => BuiltInNodes.IsOutput(n.Type)))
                        _session.SetLimit(output.Id, _frame.OutputLimit / 100);
                    _session.Inputs!.Frames[frameSource] = _frame;
                    await _session.FlushAsync();
                }
                else
                    await Start(RemotePipelineProgram.Manual(Devices, _frame, maximum), ct, frameOutputs);
                return true;
            case "control.strength":
                var levels = new[] { Clamp(RemoteProtocol.Parameter<double>(parameters, "channelA")), Clamp(RemoteProtocol.Parameter<double>(parameters, "channelB")),
                    parameters.TryGetProperty("channelC", out var c) ? Clamp(c.GetDouble()) : 0 };
                if (_session is null)
                {
                    _frame = new(levels[0], levels[1], 20, ChannelC: levels[2], OutputLimit: maximum);
                    await Start(RemotePipelineProgram.Manual(Devices, _frame, maximum), ct);
                }
                else if (_program?.FrameSource is { } source)
                {
                    _frame = _frame with
                    {
                        ChannelA = levels[0],
                        ChannelB = levels[1],
                        ChannelC = levels[2]
                    };
                    _session.Inputs!.Frames[source] = _frame;
                    await _session.FlushAsync();
                }
                else if (_program is not null)
                {
                    foreach (var (channel, nodes) in _program.Strengths)
                        foreach (var id in nodes)
                            _session.SetConstant(id, levels[channel] / 100);
                    await _session.FlushAsync();
                }
                return true;
            case "control.waveform":
                if (_session is null || _program is not { Waves.Count: > 0 })
                    throw new RemoteRpcException(-32000, "Error.RemoteNotPlaying");
                var waveform = RemoteProtocol.Parameter<WaveformPreset>(parameters, "waveform");
                _program.ValidateWaveform(waveform);
                foreach (var node in _program.Waves)
                    _session.Update(NodeControl.Create(node, "waveform", waveform));
                await _session.FlushAsync();
                return true;
            case "control.pause":
                if (_session?.State == PipelineState.Running)
                    await _session.PauseAsync();
                return true;
            case "control.resume":
                if (_session?.State == PipelineState.Paused)
                    await _session.PauseAsync();
                return true;
            case "control.stop":
                await Stop(ct);
                return true;
            default:
                throw new RemoteRpcException(-32601, "Error.RemoteMethod");
        }
    }
    private async Task Start(RemotePipelineProgram program, CancellationToken ct, IReadOnlyList<DeviceOutputSettings>? outputSettings = null)
    {
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(program.Graph, program.Assets, Devices.ToDictionary(d => d.Id));
        await Stop(ct);
        if (outputSettings is not null)
            await ConfigureOutputs(outputSettings, ct);
        _program = program;
        _session = new(devices, ownerId: owner, retainOwnership: true);
        await _session.StartAsync(plan);
        if (program.FrameSource is { } source)
        {
            _session.Inputs!.Frames[source] = _frame;
            await _session.FlushAsync();
        }
    }
    private void ValidateFrame(OutputFrame frame)
    {
        frame.Validate();
        frame.NativeProgram?.ValidateOutput(frame with
        {
            ChannelA = Math.Min(frame.ChannelA, maximum),
            ChannelB = Math.Min(frame.ChannelB, maximum),
            ChannelC = Math.Min(frame.ChannelC, maximum),
            OutputLimit = Math.Min(frame.OutputLimit, maximum)
        });
        foreach (var device in Devices)
        {
            if (frame.NativeProgram is { } native && !native.Settings.Accepts(device) || frame.Pump is not null && device.Kind != DeviceKind.Pump)
                throw new RemoteRpcException(-32602, "Error.ModeDeviceMismatch");
            if (device.IsInput && (frame.ChannelA > 0 || frame.ChannelB > 0 || frame.ChannelC > 0 || frame.Hardware is { AuxiliaryMotor: not AuxiliaryMotorMode.Off }))
                throw new RemoteRpcException(-32602, "Error.SensorOnly");
        }
    }
    private DeviceOutputSettings[] PrepareOutputs(bool gentle, HardwareOutputSettings? hardware, DgBalanceSettings? balances)
    {
        var settings = Devices.Select(device => new DeviceOutputSettings(device.Id, gentle, hardware, balances)).ToArray();
        foreach (var item in settings)
        {
            item.Validate();
            if (!item.Accepts(devices.Connections[item.DeviceId].Device))
                throw new RemoteRpcException(-32602, "Error.ProtocolSettings");
        }
        return settings;
    }
    private async Task ConfigureOutputs(IReadOnlyList<DeviceOutputSettings> settings, CancellationToken ct)
    {
        foreach (var item in settings)
            if (devices.GetOutputSettings(item.DeviceId) != item)
                await devices.SetOutputSettingsAsync(devices.Connections[item.DeviceId].Device, item, ct, owner);
    }
    private double Clamp(double value) => double.IsFinite(value) && value >= 0 ? Math.Min(value, maximum) : throw new RemoteRpcException(-32602, "Error.InvalidSettings");
    public Task Tick()
    {
        if (_session?.Error is { } error)
            throw error;
        foreach (var exported in _subscriptions)
        {
            var status = Status(exported);
            if (_lastStatus.TryGetValue(exported, out var last) && last == status)
                continue;
            _lastStatus[exported] = status;
            notify(status);
        }
        return Task.CompletedTask;
    }
    public async Task Stop(CancellationToken ct)
    {
        if (_session is not null)
            await _session.DisposeAsync();
        await devices.StopOwnedAsync(owner, ct, targets.Values.ToArray(), release: false);
        _session = null;
        _program = null;
    }
}
