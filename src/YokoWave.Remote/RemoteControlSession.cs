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
    private readonly Dictionary<(string Device, string Field), (DateTimeOffset Captured, long Clock)> _captureTimes = [];
    private DeviceDescriptor[] Devices => targets.Values.Select(id => devices.Connections[id].Device).ToArray();
    public async Task<object?> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "control.status":
                return Status(RemoteProtocol.Parameter<string>(parameters, "deviceId"));
            case "control.subscribe":
            case "control.unsubscribe":
                _ = Target(parameters);
                string subscription = RemoteProtocol.Parameter<string>(parameters, "deviceId");
                if (method == "control.subscribe")
                    _subscriptions.Add(subscription);
                else
                    _subscriptions.Remove(subscription);
                _lastStatus.Remove(subscription);
                return true;
            case "control.command":
                await devices.ExecuteCommandAsync(Target(parameters), RemoteProtocol.Parameter<DeviceCommand>(parameters, "command"), owner, cancellationToken).ConfigureAwait(false);
                return true;
            case "control.play":
            case "control.playlist":
            case "control.native":
            case "control.pump":
                var request = RemoteProtocol.Parameter<RemoteControlProgram>(parameters, "playback");
                if (method == "control.native" && request.Program.Kind != RemotePlaybackKind.NativeProgram ||
                    method == "control.pump" && request.Program.Kind != RemotePlaybackKind.Pump ||
                    method == "control.playlist" && request.Program.Playlist is not { Count: > 0 })
                {
                    throw new RemoteRpcException(-32602, "Error.PlaybackSettings");
                }

                var program = RemotePipelineProgram.Playback(Devices, request, maximum);
                var playbackOutputs = PrepareOutputs(request.Program.Gentle, request.Program.Hardware, request.Program.Balances);
                await StartAsync(program, cancellationToken, playbackOutputs).ConfigureAwait(false);
                return true;
            case "control.output":
                var frame = RemoteProtocol.Parameter<OutputFrame>(parameters, "frame");
                ValidateFrame(frame);
                var frameOutputs = PrepareOutputs(frame.Gentle, frame.Hardware, frame.DgBalances);
                if (Devices.All(device => device.IsInput))
                {
                    await StopAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                _frame = frame with
                {
                    OutputLimit = Math.Min(maximum, frame.OutputLimit)
                };
                if (_session is { State: PipelineState.Running } && _program?.FrameSource is { } frameSource)
                {
                    await ConfigureOutputsAsync(frameOutputs, cancellationToken).ConfigureAwait(false);
                    foreach (var output in _program.Graph.Nodes.Where(n => BuiltInNodes.IsOutput(n.Type)))
                        _session.SetLimit(output.Id, _frame.OutputLimit / 100);
                    _session.Inputs!.Frames[frameSource] = _frame;
                    await _session.FlushAsync().ConfigureAwait(false);
                }
                else
                {
                    await StartAsync(RemotePipelineProgram.Manual(Devices, _frame, maximum), cancellationToken, frameOutputs).ConfigureAwait(false);
                }

                return true;
            case "control.strength":
                double[] levels = new[] { Clamp(RemoteProtocol.Parameter<double>(parameters, "channelA")), Clamp(RemoteProtocol.Parameter<double>(parameters, "channelB")),
                    parameters.TryGetProperty("channelC", out var c) ? Clamp(c.GetDouble()) : 0 };
                if (_session is null)
                {
                    _frame = new(levels[0], levels[1], 20, ChannelC: levels[2], OutputLimit: maximum);
                    await StartAsync(RemotePipelineProgram.Manual(Devices, _frame, maximum), cancellationToken).ConfigureAwait(false);
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
                    await _session.FlushAsync().ConfigureAwait(false);
                }
                else if (_program is not null)
                {
                    foreach (var (channel, nodes) in _program.Strengths)
                    {
                        foreach (var id in nodes)
                            _session.SetConstant(id, levels[channel] / 100);
                    }

                    await _session.FlushAsync().ConfigureAwait(false);
                }
                return true;
            case "control.waveform":
                if (_session is null || _program is not { Waves.Count: > 0 })
                    throw new RemoteRpcException(-32000, "Error.RemoteNotPlaying");
                var waveform = RemoteProtocol.Parameter<WaveformAsset>(parameters, "waveform");
                _program.ValidateWaveform(waveform);
                foreach (var node in _program.Waves)
                    _session.Update(NodeControl.Create(node, "waveform", waveform));
                await _session.FlushAsync().ConfigureAwait(false);
                return true;
            case "control.pause":
                if (_session?.State == PipelineState.Running)
                    await _session.PauseAsync().ConfigureAwait(false);
                return true;
            case "control.resume":
                if (_session?.State == PipelineState.Paused)
                    await _session.PauseAsync().ConfigureAwait(false);
                return true;
            case "control.stop":
                await StopAsync(cancellationToken).ConfigureAwait(false);
                return true;
            default:
                throw new RemoteRpcException(-32601, "Error.RemoteMethod");
        }
    }
    public Task TickAsync()
    {
        if (_session?.Error is { } error)
            throw error;
        foreach (string exported in _subscriptions)
        {
            var status = Status(exported);
            if (_lastStatus.TryGetValue(exported, out var last) && last == status)
                continue;
            _lastStatus[exported] = status;
            notify(status);
        }
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
            await _session.DisposeAsync().ConfigureAwait(false);
        await devices.StopOwnedAsync(owner, cancellationToken, targets.Values.ToArray(), release: false).ConfigureAwait(false);
        _session = null;
        _program = null;
    }
    private RemoteStatus Status(string exported)
    {
        if (!targets.TryGetValue(exported, out string? id) || !(statusDevices ?? devices).Connections.TryGetValue(id, out var connection))
            throw new RemoteRpcException(-32004, "Error.NotConnected");
        var telemetry = connection.Telemetry;
        return new(exported, telemetry is null ? null : telemetry with { PressureCapturedAt = null, EventCapturedAt = null }, connection.Device.Battery, connection.Motion, connection.ContactA, connection.ContactB, connection.LightEnabled,
            connection.HardwareVersion, connection.SoftwareVersion,
            CaptureClock(exported, "telemetry", connection.TelemetryCapturedAt), CaptureClock(exported, "motion", connection.Motion?.CapturedAt),
            CaptureClock(exported, "pressure", telemetry?.PressureCapturedAt), CaptureClock(exported, "event", telemetry?.EventCapturedAt));
    }
    private long? CaptureClock(string device, string field, DateTimeOffset? capturedAt)
    {
        if (capturedAt is not { } captured)
            return null;
        var key = (device, field);
        if (_captureTimes.TryGetValue(key, out var previous) && previous.Captured == captured)
            return previous.Clock;
        var age = (statusDevices ?? devices).Clock.GetUtcNow() - captured;
        long timestamp = clock() - (long)age.TotalMilliseconds;
        _captureTimes[key] = (captured, timestamp);
        return timestamp;
    }
    private string Target(JsonElement parameters)
    {
        string id = RemoteProtocol.Parameter<string>(parameters, "deviceId");
        return targets.TryGetValue(id, out string? local) ? local : throw new RemoteRpcException(-32003, "Error.RemoteLease");
    }
    private async Task StartAsync(RemotePipelineProgram program, CancellationToken cancellationToken, IReadOnlyList<DeviceOutputSettings>? outputSettings = null)
    {
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(program.Graph, program.Assets, Devices.ToDictionary(d => d.Id));
        await StopAsync(cancellationToken).ConfigureAwait(false);
        if (outputSettings is not null)
            await ConfigureOutputsAsync(outputSettings, cancellationToken).ConfigureAwait(false);
        _program = program;
        _session = new(devices, ownerId: owner, retainOwnership: true);
        await _session.StartAsync(plan).ConfigureAwait(false);
        if (program.FrameSource is { } source)
        {
            _session.Inputs!.Frames[source] = _frame;
            await _session.FlushAsync().ConfigureAwait(false);
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
    private async Task ConfigureOutputsAsync(IReadOnlyList<DeviceOutputSettings> settings, CancellationToken cancellationToken)
    {
        foreach (var item in settings)
        {
            if (devices.GetOutputSettings(item.DeviceId) != item)
                await devices.SetOutputSettingsAsync(devices.Connections[item.DeviceId].Device, item, cancellationToken, owner).ConfigureAwait(false);
        }
    }
    private double Clamp(double value) => double.IsFinite(value) && value >= 0 ? Math.Min(value, maximum) : throw new RemoteRpcException(-32602, "Error.InvalidSettings");
}
