using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Pipelines;

public sealed record PlaylistNode(Guid WaveformId, Guid[]? Playlist = null, PlaybackOrder Order = PlaybackOrder.Repeat,
    int Channel = 0, double Phase = 0, bool RestartOnTrigger = false, bool StopWhenComplete = true);
public sealed record TouchNode(TouchPlayback Playback = TouchPlayback.Momentary);
public sealed record AudioNode(AudioSettings Settings);
public sealed record SensorNode(string DeviceId = "");
public sealed record DeviationNode;
public sealed record RangeMapNode(double Minimum = 5, double Maximum = 35, double OutputMinimum = 0, double OutputMaximum = 1);
public sealed record CounterNode(double Threshold = 10, int Goal = 0);
public sealed record CycleNode(double WaitSeconds = 90, double BurstSeconds = 5, bool ActivityReset = false,
    bool RequireActivity = false, bool Random = false, double MinimumWait = 30, double MaximumWait = 90,
    double MinimumBurst = 5, double MaximumBurst = 10, double MinimumStrength = .5, double MaximumStrength = 1);
public sealed record ParameterMapNode;
public sealed record AutomationNode(StrengthSettings Settings);
public sealed record PumpTimerNode(PumpOutput Settings, bool StopWhenComplete = true);
public sealed record NativeOutputNode(string DeviceId = "", NativeProgramSettings? Settings = null, bool StopWhenComplete = true, double Limit = .6);
public sealed record CommandNode(string DeviceId, DeviceCommand Command, bool OnStart = false);
public sealed record FrameInputNode;
public sealed record RouterOutputNode(string DeviceId = "", double Limit = .6);

public static partial class BuiltInNodes
{
    public const string Playlist = "waveform.playlist";
    public const string Touch = "input.touch";
    public const string Audio = "input.audio";
    public const string Sensor = "input.sensor";
    public const string Deviation = "numeric.deviation";
    public const string RangeMap = "numeric.map";
    public const string Counter = "activity.counter";
    public const string Cycle = "time.cycle";
    public const string ParameterMap = "numeric.parameters";
    public const string Automation = "numeric.automation";
    public const string PumpTimer = "time.pump";
    public const string NativeOutput = "device.native";
    public const string Command = "device.command";
    public const string FrameInput = "control.frame";
    public const string RouterOutput = "device.router";
    public static bool IsOutput(string type) => type is ChannelOutput or PumpOutput or NativeOutput or RouterOutput;
    private static bool ValidLimit(double limit) => double.IsFinite(limit) && limit is >= 0 and <= 1;
    public static NodePort[] FramePorts() => new[] { 'A', 'B', 'C' }.SelectMany(c => new[]
    {
        Out("strength" + c), Out("frequency" + c), Out("width" + c)
    }).Concat(new[] { "direction", "water", "peristalticSeconds", "waterSeconds" }.Select(Out))
        .Append(NodePort.Intent("intent", "Pipeline.Port.intent", PortDirection.Output)).ToArray();

    private static NodePort In(string id, double min = double.MinValue, double max = double.MaxValue, double value = 0, bool integer = false) =>
        NodePort.Input(id, $"Pipeline.Port.{id}", min, max, value, integer: integer);
    private static NodePort Out(string id) => NodePort.Output(id, $"Pipeline.Port.{id}");
    private static bool Valid(Action validate)
    {
        try
        {
            validate();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
    }
    private static bool Finite(params double[] values) => values.All(double.IsFinite);

    private static NodeTypeRegistry RegisterFeatures(NodeTypeRegistry registry) => registry
        .Register(SubgraphExpansion.Registration())
        .Register(new NodeRegistration<PlaylistNode>(Playlist, "Pipeline.Node.Playlist",
            [In("trigger", 0, 1), Out("level"), Out("frequency"), Out("pulseWidth")],
            c => c.WaveformId != Guid.Empty && Enum.IsDefined(c.Order) && c.Channel is >= 0 and <= 2 && double.IsFinite(c.Phase) &&
                (c.Order is not (PlaybackOrder.Sequential or PlaybackOrder.Random) || c.Playlist is { Length: > 0 and <= 100 }),
            (c, waves) => new PlaylistProcessor(c, waves), waveformReferences: c => new[] { c.WaveformId }.Concat(c.Playlist ?? [])))
        .Register(new NodeRegistration<TouchNode>(Touch, "Pipeline.Node.Touch", [Out("level"), Out("frequency"), Out("pulseWidth")],
            c => Enum.IsDefined(c.Playback), (c, _) => new TouchProcessor(c)))
        .Register(new NodeRegistration<AudioNode>(Audio, "Pipeline.Node.Audio", [Out("left"), Out("right"), Out("leftDb"), Out("rightDb")],
            c => c.Settings is not null && Valid(c.Settings.Validate), (c, _) => new AudioProcessor(c.Settings),
            participant: (context, c) => new AudioParticipant(context.NodeId, c.Settings, context.Services.Audio)))
        .Register(new NodeRegistration<SensorNode>(Sensor, "Pipeline.Node.Sensor",
            [Out("angle"), Out("steps"), Out("pressure"), Out("pressureA"), Out("pressureB"), Out("pressed"), Out("acceleration"),
             Out("angleX"), Out("angleY"), Out("angleZ"), Out("buttons"), Out("voltage"), Out("intensityA"), Out("intensityB"),
             Out("accelerationX"), Out("accelerationY"), Out("accelerationZ"), Out("gyroscopeX"), Out("gyroscopeY"), Out("gyroscopeZ"),
             Out("eventId"), Out("eventActive"), Out("parameter"), Out("modelId"), Out("modeA"), Out("modeB"), Out("indicatorColor"), Out("sequence")],
            c => !string.IsNullOrWhiteSpace(c.DeviceId), (c, _) => new SensorProcessor(), c => new(c.DeviceId, OutputBindingKind.Sensor),
            participant: (context, c) => new SensorParticipant(context, c)))
        .Register(new NodeRegistration<DeviationNode>(Deviation, "Pipeline.Node.Deviation", [In("value", 0, 180), Out("value")],
            _ => true, (_, _) => new DeviationProcessor()))
        .Register(new NodeRegistration<RangeMapNode>(RangeMap, "Pipeline.Node.Map", [In("value"), Out("value")],
            c => Finite(c.Minimum, c.Maximum, c.OutputMinimum, c.OutputMaximum) && c.Maximum > c.Minimum,
            (c, _) => new RangeMapProcessor(c)))
        .Register(new NodeRegistration<CounterNode>(Counter, "Pipeline.Node.Counter", [In("angle", 0, 180), In("steps", 0, 65535), Out("activity"), Out("count")],
            c => double.IsFinite(c.Threshold) && c.Threshold is > 0 and <= 180 && c.Goal is >= 0 and <= 1000000,
            (c, _) => new CounterProcessor(c)))
        .Register(new NodeRegistration<CycleNode>(Cycle, "Pipeline.Node.Cycle", [In("activity", 0, 1), Out("gate"), Out("trigger"), Out("remaining")],
            c => Finite(c.WaitSeconds, c.BurstSeconds, c.MinimumWait, c.MaximumWait, c.MinimumBurst, c.MaximumBurst, c.MinimumStrength, c.MaximumStrength) &&
                c.WaitSeconds is >= 1 and <= 7200 && c.BurstSeconds is >= 1 and <= 60 && c.MinimumWait is >= 1 and <= 7200 &&
                c.MaximumWait >= c.MinimumWait && c.MaximumWait <= 7200 && c.MinimumBurst is >= 1 and <= 60 &&
                c.MaximumBurst >= c.MinimumBurst && c.MaximumBurst <= 60 && c.MinimumStrength is >= 0 and <= 1 &&
                c.MaximumStrength >= c.MinimumStrength && c.MaximumStrength <= 1, (c, _) => new CycleProcessor(c)))
        .Register(new NodeRegistration<ParameterMapNode>(ParameterMap, "Pipeline.Node.Parameters", [In("value", 0, 1), Out("level"), Out("frequency"), Out("pulseWidth")],
            _ => true, (_, _) => new ParameterMapProcessor()))
        .Register(new NodeRegistration<AutomationNode>(Automation, "Pipeline.Node.Automation", [In("value", 0, 1), In("boost", 0, 1), Out("value")],
            c => c.Settings is not null && Valid(c.Settings.Validate), (c, _) => new AutomationProcessor(c.Settings)))
        .Register(new NodeRegistration<PumpTimerNode>(PumpTimer, "Pipeline.Node.PumpTimer",
            [Out("direction"), Out("water"), Out("peristalticSeconds"), Out("waterSeconds")],
            c => c.Settings is not null && Valid(c.Settings.Validate), (c, _) => new PumpTimerProcessor(c)))
        .Register(new NodeRegistration<NativeOutputNode>(NativeOutput, "Pipeline.Node.Native",
            [In("strengthA", 0, 1), In("strengthB", 0, 1), In("strengthC", 0, 1),
             In("presetA", 0, 255, 1, true), In("presetB", 0, 255, 1, true), In("presetC", 0, 255, 1, true)],
            c => !string.IsNullOrWhiteSpace(c.DeviceId) && ValidLimit(c.Limit) && Valid(() => (c.Settings ?? new()).Validate()),
            (c, _) => new NativeOutputProcessor(c), c => new(c.DeviceId, OutputBindingKind.Native)))
        .Register(new NodeRegistration<CommandNode>(Command, "Pipeline.Node.Command", [In("trigger", 0, 1)],
            c => !string.IsNullOrWhiteSpace(c.DeviceId) && c.Command is not null && Valid(c.Command.Validate),
            (c, _) => new CommandProcessor(c), c => new(c.DeviceId, OutputBindingKind.Command)))
        .Register(new NodeRegistration<FrameInputNode>(FrameInput, "Pipeline.Node.Control", FramePorts(),
            _ => true, (_, _) => new FrameSignalProcessor()))
        .Register(new NodeRegistration<RouterOutputNode>(RouterOutput, "Pipeline.Node.Router", new[] { 'A', 'B', 'C' }.SelectMany(c => new[]
        {
            In("strength" + c, 0, 1), In("frequency" + c, 1, 100, 20), In("width" + c, 0, 1, 1), In("limit" + c, 0, 1, 1)
        }).Concat(new[] { In("direction", 0, 2, integer: true), In("water", 0, 1, integer: true), In("peristalticSeconds", 0, 65535, integer: true), In("waterSeconds", 0, 65535, integer: true) })
            .Append(NodePort.Intent("intent", "Pipeline.Port.intent", PortDirection.Input)).ToArray(),
            c => !string.IsNullOrWhiteSpace(c.DeviceId) && ValidLimit(c.Limit), (c, _) => new RouterOutputProcessor(c), c => new(c.DeviceId, OutputBindingKind.Router)));
}
