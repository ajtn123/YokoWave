using YokoWave.Core;

namespace YokoWave.Pipelines;

internal sealed class NativeOutputProcessor(NativeOutputNode settings) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context)
    {
        var program = settings.Settings ?? new();
        var a = context.Input("strengthA");
        var b = context.Input("strengthB");
        var c = context.Input("strengthC");
        var inputs = new List<NumericSignal> { a, b, c };
        if (program.Kind is NativeProgramKind.ElectricalPreset or NativeProgramKind.MotorPreset)
        {
            var pa = context.Input("presetA", program.PresetA);
            var pb = context.Input("presetB", program.PresetB);
            var pc = context.Input("presetC", program.PresetC);
            inputs.AddRange([pa, pb, pc]);
            if (new[] { pa, pb, pc }.Any(s => !s.IsValid(context.Now) || s.At(0) != Math.Truncate(s.At(0)) ||
                s.At(0) > (program.Kind == NativeProgramKind.MotorPreset ? 255 : 16)))
            {
                context.Zero(settings.DeviceId);
                return;
            }
            program = program with
            {
                PresetA = (int)pa.At(0),
                PresetB = (int)pb.At(0),
                PresetC = (int)pc.At(0)
            };
        }
        if (inputs.Any(s => !s.IsValid(context.Now) || s.Simulated && !context.IsSimulatedTarget(settings.DeviceId)))
        {
            context.Zero(settings.DeviceId);
            return;
        }
        context.Native(settings, a.At(0), b.At(0), c.At(0), program);
        if (settings.StopWhenComplete && program is { Kind: NativeProgramKind.ElectricalBuffered, Repeat: false })
            context.Completion(context.Elapsed.TotalSeconds >= Math.Max(program.BufferA!.Count, program.BufferB!.Count) / 10d, "Reason.WaveComplete");
    }
}

internal sealed class CommandProcessor(CommandNode settings) : ISignalProcessor, IInteractiveProcessor, ISuspendableProcessor
{
    private bool _started, _last, _requested;
    public void Suspend()
    {
        _started = false;
        _last = false;
        _requested = false;
    }
    public void Update(NodeControl control)
    {
        if (control.Action == "trigger")
            _requested = true;
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        var trigger = context.Input("trigger");
        var high = trigger.IsValid(context.Now) && trigger.At(0) > 0;
        if (trigger.IsValid(context.Now) && (!_started && settings.OnStart || high && !_last || _requested) && (!trigger.Simulated || context.IsSimulatedTarget(settings.DeviceId)))
            context.Command(settings.DeviceId, settings.Command);
        _started = true;
        _last = high;
        _requested = false;
    }
}

public sealed class FrameSignalProcessor : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context)
    {
        if (!context.Inputs.Frames.TryGetValue(context.NodeId, out var frame))
            return;
        context.IntentOutput("intent", frame);
        for (var channel = 0; channel < 3; channel++)
        {
            var suffix = ((char)('A' + channel)).ToString();
            var samples = Enumerable.Range(0, 4).Select(i => WaveformRendering.Sample(frame, channel, i)).ToArray();
            var strength = channel == 0 ? frame.ChannelA : channel == 1 ? frame.ChannelB : frame.ChannelC;
            context.Output("strength" + suffix, new(samples.Select(s => strength / 100 * s.Level).ToArray(), DateTimeOffset.MaxValue));
            context.Output("frequency" + suffix, new(samples.Select(s => s.FrequencyHz).ToArray(), DateTimeOffset.MaxValue));
            context.Output("width" + suffix, new(samples.Select(s => s.PulseWidth * s.ChannelMagnitude(channel)).ToArray(), DateTimeOffset.MaxValue));
        }
        context.Output("direction", NumericSignal.Constant((int)(frame.Pump?.Direction ?? PumpDirection.Off)));
        context.Output("water", NumericSignal.Constant(frame.Pump?.Water == true ? 1 : 0));
        context.Output("peristalticSeconds", NumericSignal.Constant(frame.Pump?.PeristalticSeconds ?? 0));
        context.Output("waterSeconds", NumericSignal.Constant(frame.Pump?.WaterSeconds ?? 0));
    }
}

internal sealed class RouterOutputProcessor(RouterOutputNode settings) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context)
    {
        var intent = context.IntentInput("intent");
        if (intent?.Pump is { } pump)
        {
            context.Pump(new(settings.DeviceId, settings.Limit), context.Input("direction", (int)pump.Direction), context.Input("water", pump.Water ? 1 : 0),
                context.Input("peristalticSeconds", pump.PeristalticSeconds), context.Input("waterSeconds", pump.WaterSeconds));
            return;
        }
        var strength = new NumericSignal[3];
        var frequency = new NumericSignal[3];
        var width = new NumericSignal[3];
        var limits = new NumericSignal[3];
        for (var channel = 0; channel < 3; channel++)
        {
            var suffix = ((char)('A' + channel)).ToString();
            strength[channel] = context.Input("strength" + suffix);
            frequency[channel] = context.Input("frequency" + suffix);
            width[channel] = context.Input("width" + suffix);
            limits[channel] = context.Input("limit" + suffix);
        }
        if (intent?.NativeProgram is { } program)
        {
            var valid = strength.Concat(limits).All(s => s.IsValid(context.Now) && (!s.Simulated || context.IsSimulatedTarget(settings.DeviceId)));
            if (!valid)
            {
                context.Zero(settings.DeviceId);
                return;
            }
            double Level(int channel) => valid ? Math.Min(strength[channel].At(0), limits[channel].At(0)) : 0;
            context.RouteNative(new(settings.DeviceId, program.Settings, intent.Hardware, intent.DgBalances, intent.Gentle, Limit: settings.Limit), Level(0), Level(1), Level(2), program);
            return;
        }
        for (var channel = 0; channel < 3; channel++)
            context.Channel(new(settings.DeviceId, channel, intent?.Gentle ?? false, intent?.Hardware, intent?.DgBalances, settings.Limit),
                strength[channel], frequency[channel], width[channel], limits[channel]);
    }
}
