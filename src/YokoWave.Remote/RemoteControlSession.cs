using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Pipelines;

namespace YokoWave.Remote;

/// <summary>RPC translates requests to ordinary compiled graphs, including manual output.</summary>
internal sealed class RemoteControlSession(DeviceController devices, Guid owner, IReadOnlyDictionary<string, string> targets,
    double maximum, Action<RemoteStatus> notify, DeviceController? statusDevices = null)
{
    private PipelineSession? _session;
    private RemotePipelineProgram? _program;
    private OutputFrame _frame = OutputFrame.Zero;
    private readonly HashSet<string> _subscriptions = [];
    private readonly Dictionary<string, RemoteStatus> _lastStatus = [];
    private DeviceDescriptor[] Devices => targets.Values.Select(id => devices.Connections[id].Device).ToArray();
    private RemoteStatus Status(string exported)
    {
        if (!targets.TryGetValue(exported, out var id) || !(statusDevices ?? devices).Connections.TryGetValue(id, out var connection))
            throw new RemoteRpcException(-32004, "Error.NotConnected");
        return new(exported, connection.Telemetry, connection.Device.Battery, connection.Motion, connection.ContactA, connection.ContactB, connection.LightEnabled,
            connection.HardwareVersion, connection.SoftwareVersion);
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
                await ConfigureOutputs(request.Program.Gentle, request.Program.Hardware, request.Program.Balances, ct);
                await Start(RemotePipelineProgram.Playback(Devices, request, maximum), ct);
                return true;
            case "control.output":
                var frame = RemoteProtocol.Parameter<OutputFrame>(parameters, "frame");
                if (!double.IsFinite(frame.OutputLimit) || frame.OutputLimit is < 0 or > 100)
                    throw new RemoteRpcException(-32602, "Error.InvalidSettings");
                await ConfigureOutputs(frame.Gentle, frame.Hardware, frame.DgBalances, ct);
                _frame = frame with
                {
                    OutputLimit = Math.Min(maximum, frame.OutputLimit)
                };
                if (_session is { State: PipelineState.Running } && _program?.FrameSource is { } frameSource)
                {
                    foreach (var output in _program.Graph.Nodes.Where(n => BuiltInNodes.IsOutput(n.Type)))
                        _session.SetLimit(output.Id, _frame.OutputLimit / 100);
                    _session.Inputs!.Frames[frameSource] = _frame;
                    await _session.FlushAsync();
                }
                else
                    await Start(RemotePipelineProgram.Manual(Devices, _frame, maximum), ct);
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
                waveform.Validate();
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
    private async Task Start(RemotePipelineProgram program, CancellationToken ct)
    {
        var plan = new PipelineCompiler(BuiltInNodes.CreateRegistry()).Compile(program.Graph, program.Assets, Devices.ToDictionary(d => d.Id));
        await Stop(ct);
        _program = program;
        _session = new(devices, ownerId: owner, retainOwnership: true);
        await _session.StartAsync(plan);
        if (program.FrameSource is { } source)
        {
            _session.Inputs!.Frames[source] = _frame;
            await _session.FlushAsync();
        }
    }
    private async Task ConfigureOutputs(bool gentle, HardwareOutputSettings? hardware, DgBalanceSettings? balances, CancellationToken ct)
    {
        var settings = Devices.Select(device => (Device: device, Settings: new DeviceOutputSettings(device.Id, gentle, hardware, balances))).ToArray();
        foreach (var item in settings)
        {
            item.Settings.Validate();
            if (!item.Settings.Accepts(item.Device))
                throw new RemoteRpcException(-32602, "Error.ProtocolSettings");
        }
        foreach (var item in settings)
            if (devices.GetOutputSettings(item.Device.Id) != item.Settings)
                await devices.SetOutputSettingsAsync(item.Device, item.Settings, ct, owner);
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
