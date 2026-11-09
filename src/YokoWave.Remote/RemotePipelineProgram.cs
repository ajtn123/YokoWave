using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;
using YokoWave.Pipelines;

namespace YokoWave.Remote;

internal sealed record RemotePipelineProgram(PipelineDocument Graph, WaveformAsset[] Assets,
    Dictionary<int, List<Guid>> Strengths, List<Guid> Waves, Guid? FrameSource = null)
{
    public void ValidateWaveform(WaveformAsset waveform)
    {
        waveform.Validate();
        foreach (var node in Graph.Nodes.Where(n => n.Type == BuiltInNodes.Playlist))
        {
            if (!node.Configuration.Deserialize<PlaylistNode>(PipelineJson.Options)!.AcceptsWaveform(waveform.Id))
                throw new LocalizedArgumentException("Error.PlaylistWave");
        }
    }
    public static RemotePipelineProgram Manual(IEnumerable<DeviceDescriptor> targets, OutputFrame frame, double maximum)
    {
        var builder = new PipelineBuilder(L10n.Get("Remote.Control"), Math.Min(maximum, frame.OutputLimit) / 100);
        var source = builder.Add(BuiltInNodes.FrameInput, new FrameInputNode());
        foreach (var device in targets)
        {
            var sink = builder.Add(BuiltInNodes.RouterOutput, new RouterOutputNode(device.Id, Math.Min(maximum, frame.OutputLimit) / 100));
            foreach (var port in BuiltInNodes.FramePorts())
                builder.Connect(source, port.Id, sink, port.Id);
        }
        return new(builder.Build(), [], [], [], source.Id);
    }

    public static RemotePipelineProgram Playback(IEnumerable<DeviceDescriptor> targets, RemoteControlProgram request, double maximum)
    {
        var settings = request.Program;
        settings.Validate();
        var waveform = request.Waveform ?? settings.Playlist?.FirstOrDefault() ?? new(Guid.NewGuid(), L10n.Get("Remote.Control"), WaveShape.Pulse, 1, 20);
        waveform.Validate();
        var assets = new[] { waveform, settings.ChannelBWaveform }.OfType<WaveformAsset>().Concat(settings.Playlist ?? []).DistinctBy(w => w.Id).ToArray();
        var builder = new PipelineBuilder(L10n.Get("Remote.Control"), Math.Min(maximum, settings.OutputLimit) / 100);
        Dictionary<int, List<Guid>> strengths = [];
        List<Guid> waves = [];
        double[] levels = new[] { request.ChannelA, request.ChannelB, request.ChannelC };
        NodeDefinition Strength(int channel)
        {
            if (!double.IsFinite(levels[channel]) || levels[channel] < 0)
                throw new LocalizedArgumentException("Error.InvalidSettings");
            var node = builder.Add(BuiltInNodes.Constant, new ConstantNode(Math.Min(levels[channel], maximum) / 100));
            if (!strengths.TryGetValue(channel, out var list))
                strengths[channel] = list = [];
            list.Add(node.Id);
            return node;
        }
        foreach (var device in targets)
        {
            if (settings.Kind == RemotePlaybackKind.Pump)
            {
                var timer = builder.Add(BuiltInNodes.PumpTimer, new PumpTimerNode(settings.Pump ?? new()));
                var sink = builder.Add(BuiltInNodes.PumpOutput, new PumpOutputNode(device.Id, Math.Min(maximum, settings.OutputLimit) / 100));
                foreach (string? port in new[] { "direction", "water", "peristalticSeconds", "waterSeconds" })
                    builder.Connect(timer, port, sink, port);
                continue;
            }
            if (settings.Kind == RemotePlaybackKind.NativeProgram)
            {
                if (settings.NativeProgram is { Kind: NativeProgramKind.MotorPreset } motor)
                {
                    double cap = Math.Min(maximum, settings.OutputLimit);
                    double Level(int channel, int selected) => device.HasChannel(channel) && selected > 0 ? Math.Min(levels[channel], cap) : 0;
                    new NativeProgramOutput(motor, 0, request.DurationSeconds).ValidateOutput(new(
                        Level(0, motor.PresetA), Level(1, motor.PresetB), 20, ChannelC: Level(2, motor.PresetC), OutputLimit: cap, Hardware: settings.Hardware));
                }
                var sink = builder.Add(BuiltInNodes.NativeOutput, new NativeOutputNode(device.Id, settings.NativeProgram, Limit: Math.Min(maximum, settings.OutputLimit) / 100));
                for (int channel = 0; channel < 3; channel++)
                {
                    if (device.HasChannel(channel))
                        builder.Connect(Strength(channel), "value", sink, "strength" + (char)('A' + channel));
                }

                continue;
            }
            for (int channel = 0; channel < 3; channel++)
            {
                if (!device.HasChannel(channel))
                    continue;
                var asset = channel == 1 ? settings.ChannelBWaveform ?? waveform : waveform;
                var wave = builder.Add(BuiltInNodes.Playlist, new PlaylistNode(asset.Id,
                    settings.Playlist?.Select(w => w.Id).ToArray(), settings.Order, Phase: channel == 1 && settings.ChannelBWaveform is null && settings.OffsetChannelB ? .5 : 0));
                waves.Add(wave.Id);
                var strength = Strength(channel);
                var automation = builder.Add(BuiltInNodes.Automation, new AutomationNode(settings.Strength ?? new()));
                var gain = builder.Add(BuiltInNodes.Gain, new GainNode());
                var output = builder.Add(BuiltInNodes.ChannelOutput, new ChannelOutputNode(device.Id, channel, Math.Min(maximum, settings.OutputLimit) / 100));
                builder.Connect(strength, "value", automation, "value");
                builder.Connect(automation, "value", gain, "gain");
                builder.Connect(wave, WaveformPorts.Level(channel < asset.ChannelCount ? channel : 0), gain, "value");
                builder.Connect(gain, "value", output, "strength");
                builder.Connect(wave, "frequency", output, "frequency");
                builder.Connect(wave, "pulseWidth", output, "pulseWidth");
            }
        }
        return new(builder.Build() with
        {
            TimerEnabled = request.DurationSeconds.HasValue,
            DurationSeconds = request.DurationSeconds ?? 90
        }, assets, strengths, waves);
    }
}
