namespace YokoWave.Core;

internal sealed class PumpModeRuntime : PlaybackModeRuntime
{
    private PumpOutput? _previous;
    private double _peristalticStart, _waterStart;
    private bool _complete;
    public override string? CompletionReason(int targetCount) => _complete ? "Reason.PumpComplete" : null;
    public override OutputFrame Render(PlaybackContext context, OutputFrame frame)
    {
        var pump = context.Pump;
        pump.Validate();
        var elapsed = context.Elapsed.TotalSeconds;
        if (_previous is { } previous)
        {
            if (pump.Direction != previous.Direction || pump.PeristalticSeconds != previous.PeristalticSeconds) _peristalticStart = elapsed;
            if (pump.Water != previous.Water || pump.WaterSeconds != previous.WaterSeconds) _waterStart = elapsed;
        }
        _previous = pump;
        var a = Remaining(pump.PeristalticSeconds, elapsed - _peristalticStart);
        var b = Remaining(pump.WaterSeconds, elapsed - _waterStart);
        var direction = a == 0 && pump.PeristalticSeconds > 0 ? PumpDirection.Off : pump.Direction;
        var water = pump.Water && (pump.WaterSeconds == 0 || b > 0);
        _complete = (pump.Direction != PumpDirection.Off || pump.Water) && direction == PumpDirection.Off && !water;
        return frame with { Pump = pump with { Direction = direction, Water = water, PeristalticSeconds = a, WaterSeconds = b } };
    }
    private static int Remaining(int duration, double elapsed) => duration == 0 ? 0 : (int)Math.Max(0, Math.Ceiling(duration - elapsed));
}
