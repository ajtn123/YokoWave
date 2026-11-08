namespace YokoWave.Core;

/// <summary>Per-target freshness, calibration and movement sampling; never persisted as device settings.</summary>
internal sealed class MotionTracker
{
    public double? Baseline { get; private set; }
    public bool Fresh { get; private set; }
    public int Movements { get; private set; }
    private double? _previousAngle;
    private long _sequence = -1;
    private int? _steps;
    private double _lastCheck;

    public void Invalidate(bool recalibrate = false)
    {
        Fresh = false; _previousAngle = null; _sequence = -1;
        if (recalibrate) Baseline = null;
    }
    public bool Read(MotionReading? reading, double elapsed, TimeProvider clock, out bool acquired)
    {
        acquired = false;
        if (reading is null || !double.IsFinite(reading.Angle) || reading.Angle is < 0 or > 180 || !PlaybackInput.IsFresh(reading.CapturedAt, clock))
        { Invalidate(); return false; }
        if (!Fresh)
        {
            Fresh = acquired = true; _previousAngle = reading.Angle; _sequence = reading.Sequence;
            _steps = reading.Steps; _lastCheck = elapsed; Baseline ??= reading.Angle;
        }
        return true;
    }
    public bool DetectMovement(MotionReading reading, double elapsed, double threshold)
    {
        if (reading.Sequence == _sequence || elapsed - _lastCheck < .7) return false;
        var changed = Math.Abs(reading.Angle - (_previousAngle ?? reading.Angle)) >= threshold ||
            reading.Steps is { } steps && _steps is { } previous && steps != previous;
        if (changed) Movements++;
        _previousAngle = reading.Angle; _sequence = reading.Sequence; _steps = reading.Steps; _lastCheck = elapsed;
        return changed;
    }
}
