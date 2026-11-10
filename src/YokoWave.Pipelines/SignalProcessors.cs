using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Pipelines;

internal static class Signals
{
    public static NumericSignal Map(NumericSignal signal, Func<double, double> map) => new(signal.Samples.Select(map).ToArray(), signal.ValidUntil, signal.Simulated, signal.Sequence);
    public static bool Fresh(DateTimeOffset capturedAt, DateTimeOffset now) => now - capturedAt is var age && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(2);
    public static void Parameters(NodeEvaluationContext context, NumericSignal level)
    {
        context.Output("level", level);
        context.Output("frequency", Map(level, value => Math.Max(2, 100 - Math.Ceiling(value * 100))));
        context.Output("pulseWidth", Map(level, value => Math.Ceiling(value * 100) / 100));
    }
}

internal sealed class RangeMapProcessor(RangeMapNode settings) : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => context.Output("value", Signals.Map(context.Input("value"), value =>
        settings.OutputMinimum + Math.Clamp((value - settings.Minimum) / (settings.Maximum - settings.Minimum), 0, 1) * (settings.OutputMaximum - settings.OutputMinimum)));
}

internal sealed class ParameterMapProcessor : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context) => Signals.Parameters(context, context.Input("value"));
}

internal sealed class DeviationProcessor : ISignalProcessor, IInteractiveProcessor
{
    private double? _baseline;
    public void Update(NodeControl control)
    {
        if (control.Action == "calibrate")
            _baseline = null;
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        var value = context.Input("value");
        if (value.IsValid(context.Now))
            _baseline ??= value.At(0);
        var deviation = _baseline is { } baseline ? Signals.Map(value, v => Math.Abs(v - baseline)) : NumericSignal.Missing;
        context.Output("value", deviation);
        context.Status(new(value.IsValid(context.Now) ? "Tracking" : "Waiting", deviation.IsValid(context.Now) ? deviation.At(0) : 0));
    }
}

internal sealed class AudioProcessor(AudioSettings settings) : ISignalProcessor, ISuspendableProcessor
{
    private readonly AudioEnvelope _left = new();
    private readonly AudioEnvelope _right = new();
    private long _sequence = -1;
    private double _a;
    private double _b;
    public void Suspend()
    {
        _left.Reset();
        _right.Reset();
        _sequence = -1;
        _a = _b = 0;
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        if (!context.Inputs.Audio.TryGetValue(context.NodeId, out var audio) || audio is null || !Signals.Fresh(audio.CapturedAt, context.Now) ||
            !double.IsFinite(audio.LeftDb) || !double.IsFinite(audio.RightDb) || !double.IsFinite(audio.LeftOnset) || !double.IsFinite(audio.RightOnset) ||
            audio.LeftDb is < -120 or > 0 || audio.RightDb is < -120 or > 0 || audio.LeftOnset is < 0 or > 1 || audio.RightOnset is < 0 or > 1)
        {
            Suspend();
            foreach (string? port in new[] { "left", "right", "leftDb", "rightDb" })
                context.Output(port, NumericSignal.Missing);
            context.Status(new("Waiting"));
            return;
        }
        if (_sequence != audio.Sequence)
        {
            _sequence = audio.Sequence;
            _a = _left.Process(audio.LeftDb, audio.LeftOnset, settings);
            _b = _right.Process(audio.RightDb, audio.RightOnset, settings);
        }
        var until = audio.CapturedAt.AddSeconds(2);
        context.Output("left", new([_a], until, Sequence: audio.Sequence));
        context.Output("right", new([_b], until, Sequence: audio.Sequence));
        context.Output("leftDb", new([audio.LeftDb], until, Sequence: audio.Sequence));
        context.Output("rightDb", new([audio.RightDb], until, Sequence: audio.Sequence));
        context.Status(new(Math.Max(_a, _b) > 0 ? "Tracking" : "Safe", Math.Max(_a, _b)));
    }
}

internal sealed class TouchProcessor(TouchNode settings) : ISignalProcessor, IInteractiveProcessor, ISuspendableProcessor
{
    private TouchPoint _point = new(20, .5, false);
    private readonly List<WaveformSample> _recording = [];
    private long _bucket = -1;
    private bool _touched;
    private bool _released;
    private double _replayEpoch;
    public void Update(NodeControl control)
    {
        if (control.Action != "touch")
            return;
        var next = control.Read<TouchPoint>();
        if (!double.IsFinite(next.Frequency) || next.Frequency is < 1 or > 100 || !double.IsFinite(next.PulseWidth) || next.PulseWidth is < 0 or > 1)
            throw new LocalizedArgumentException("Error.MotionInput");
        if (next.Held && !_point.Held)
        {
            _recording.Clear();
            _bucket = -1;
        }
        _released = _point.Held && !next.Held;
        _touched |= next.Held;
        _point = next;
    }
    public void Suspend() => _point = _point with { Held = false };
    public void Evaluate(NodeEvaluationContext context)
    {
        double elapsed = context.Elapsed.TotalSeconds;
        if (_released)
        {
            _replayEpoch = elapsed;
            _released = false;
        }
        var sample = new WaveformSample(1, _point.Frequency, _point.PulseWidth);
        long bucket = (long)Math.Floor(elapsed * 10);
        if (_point.Held && bucket != _bucket && _recording.Count < 256)
        {
            _recording.Add(sample);
            _bucket = bucket;
        }
        bool active = _point.Held || _touched && settings.Playback == TouchPlayback.HoldLast;
        if (!active && settings.Playback == TouchPlayback.RecordedLoop && _recording.Count > 0)
        {
            sample = _recording[(int)Math.Max(0, Math.Floor((elapsed - _replayEpoch) * 10)) % _recording.Count];
            active = true;
        }
        context.Output("level", NumericSignal.Constant(active ? sample.Level : 0));
        context.Output("frequency", NumericSignal.Constant(sample.FrequencyHz));
        context.Output("pulseWidth", NumericSignal.Constant(sample.PulseWidth));
        context.Status(new(active ? "Tracking" : "Safe", active ? 1 : 0, Recording: _recording.ToArray()));
    }
}

internal sealed class SensorProcessor : ISignalProcessor
{
    public void Evaluate(NodeEvaluationContext context)
    {
        if (!context.Inputs.Sensors.TryGetValue(context.NodeId, out var snapshot))
        {
            context.Status(new("Waiting"));
            return;
        }
        var connection = snapshot;
        void Publish(string port, double? value, DateTimeOffset? capturedAt, long sequence = 0)
        {
            context.Output(port, value is { } number && capturedAt is { } captured && Signals.Fresh(captured, context.Now)
                ? new([number], captured.AddSeconds(2), snapshot.Simulated, sequence) : NumericSignal.Missing);
        }
        Publish("angle", connection.Motion?.Angle, connection.Motion?.CapturedAt, connection.Motion?.Sequence ?? 0);
        Publish("steps", connection.Motion?.Steps ?? connection.Steps, connection.Motion?.CapturedAt ?? connection.TelemetryCapturedAt);
        var data = connection.Telemetry;
        var captured = connection.TelemetryCapturedAt;
        Publish("pressure", data?.PressureKpa, data?.PressureCapturedAt ?? captured);
        Publish("pressureA", data?.PressureA, captured);
        Publish("pressureB", data?.PressureB, captured);
        Publish("pressed", data?.Pressed is { } pressed ? pressed ? 1 : 0 : null, captured);
        Publish("acceleration", data?.Acceleration, captured);
        Publish("angleX", data?.AngleX, captured);
        Publish("angleY", data?.AngleY, captured);
        Publish("angleZ", data?.AngleZ, captured);
        Publish("buttons", data?.Buttons, captured);
        Publish("voltage", data?.VoltageVolts, captured);
        Publish("intensityA", data?.IntensityA, captured);
        Publish("intensityB", data?.IntensityB, captured);
        Publish("accelerationX", data?.AccelerationAxes?.X, captured);
        Publish("accelerationY", data?.AccelerationAxes?.Y, captured);
        Publish("accelerationZ", data?.AccelerationAxes?.Z, captured);
        Publish("gyroscopeX", data?.GyroscopeAxes?.X, captured);
        Publish("gyroscopeY", data?.GyroscopeAxes?.Y, captured);
        Publish("gyroscopeZ", data?.GyroscopeAxes?.Z, captured);
        var eventAt = data?.EventCapturedAt ?? captured;
        long eventSequence = data?.EventSequence ?? eventAt?.UtcTicks ?? 0;
        Publish("eventId", data?.EventId, eventAt, eventSequence);
        Publish("eventActive", data?.EventActive is { } active ? active ? 1 : 0 : null, eventAt, eventSequence);
        Publish("parameter", data?.Parameter, captured);
        Publish("modelId", data?.ModelId, captured);
        Publish("modeA", data?.ModeA, captured);
        Publish("modeB", data?.ModeB, captured);
        Publish("indicatorColor", data?.IndicatorColor, captured);
        Publish("sequence", data?.Sequence, captured);
        context.Status(new("Tracking", Detail: connection.Motion?.Angle.ToString("0.0")));
    }
}
