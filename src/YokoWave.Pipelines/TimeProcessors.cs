using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Pipelines;

internal sealed class PlaylistProcessor : ISignalProcessor, IInteractiveProcessor
{
    private readonly PlaylistNode _settings;
    private readonly IReadOnlyDictionary<Guid, WaveformPreset> _assets;
    private readonly Random _random = new();
    private WaveformPreset _current;
    private double _epoch;
    private bool _restart, _trigger;
    public PlaylistProcessor(PlaylistNode settings, IReadOnlyDictionary<Guid, WaveformPreset> assets)
    {
        _settings = settings;
        _assets = assets;
        _current = assets[settings.WaveformId];
    }
    public void Update(NodeControl control)
    {
        if (control.Action != "waveform")
            return;
        var waveform = control.Read<WaveformPreset>();
        waveform.Validate();
        if (_settings.Order is PlaybackOrder.Sequential or PlaybackOrder.Random && !(_settings.Playlist?.Contains(waveform.Id) ?? false))
            throw new LocalizedOperationException("Error.PlaylistWave");
        _current = waveform;
        _restart = true;
    }
    private void Next()
    {
        var playlist = (_settings.Playlist ?? [_current.Id]).Select(id => _assets[id]).ToArray();
        if (_settings.Order == PlaybackOrder.Random && playlist.Length > 1)
        {
            var choices = playlist.Where(wave => wave.Id != _current.Id).ToArray();
            _current = choices[_random.Next(choices.Length)];
        }
        else if (_settings.Order == PlaybackOrder.Sequential)
            _current = playlist[(Array.FindIndex(playlist, w => w.Id == _current.Id) + 1) % playlist.Length];
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        var now = context.Elapsed.TotalSeconds;
        var trigger = context.Input("trigger");
        var high = trigger.IsValid(context.Now) && trigger.At(0) > 0;
        if (_restart || _settings.RestartOnTrigger && high && !_trigger)
        {
            if (!_restart)
                Next();
            _epoch = now;
            _restart = false;
        }
        _trigger = high;
        var elapsed = now - _epoch;
        if (elapsed >= _current.PeriodSeconds && _settings.Order is PlaybackOrder.Sequential or PlaybackOrder.Random && !_settings.RestartOnTrigger)
        {
            Next();
            _epoch = now;
            elapsed = 0;
        }
        var completed = _settings.Order == PlaybackOrder.Once && elapsed >= _current.PeriodSeconds;
        if (_settings.StopWhenComplete && _settings.Order == PlaybackOrder.Once)
            context.Completion(completed, "Reason.WaveComplete");
        var samples = WaveformGenerator.Window(_current, elapsed, _settings.Phase, repeat: _settings.Order != PlaybackOrder.Once);
        WaveformPorts.Write(context, _current, samples, completed);
        var position = _settings.Order == PlaybackOrder.Once ? elapsed : elapsed % _current.PeriodSeconds;
        context.Status(new(completed ? "Safe" : "Tracking", Waveform: _current.Id, RemainingSeconds: Math.Max(0, _current.PeriodSeconds - position)));
    }
}

internal sealed class CounterProcessor(CounterNode settings) : ISignalProcessor, ISuspendableProcessor, IInteractiveProcessor
{
    private double? _previousAngle;
    private double _lastCheck;
    private long _sequence = -1;
    private double? _steps;
    private long _stepSequence = -1;
    private bool _sampled;
    private int _movements;
    public void Suspend()
    {
        _previousAngle = null;
        _sequence = -1;
        _stepSequence = -1;
        _sampled = false;
        _steps = null;
    }
    public void Update(NodeControl control)
    {
        if (control.Action == "calibrate")
            Suspend();
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        if (settings.Goal > 0)
            context.Completion(_movements >= settings.Goal, "Reason.MovementGoal");
        var angle = context.IsConnected("angle") ? context.Input("angle") : NumericSignal.Missing;
        var steps = context.IsConnected("steps") ? context.Input("steps") : null;
        var hasAngle = angle.IsValid(context.Now);
        var hasSteps = steps?.IsValid(context.Now) == true;
        if (!hasAngle && !hasSteps)
        {
            Suspend();
            context.Output("activity", NumericSignal.Missing);
            context.Output("count", NumericSignal.Missing);
            context.Status(new("Waiting", Count: _movements));
            return;
        }
        var elapsed = context.Elapsed.TotalSeconds;
        var value = hasAngle ? angle.At(0) : (double?)null;
        var currentSteps = hasSteps ? steps!.At(0) : (double?)null;
        var source = hasAngle ? angle : steps!;
        var movement = false;
        if (!_sampled)
        {
            _sampled = true;
            _previousAngle = value;
            _sequence = angle.Sequence;
            _lastCheck = elapsed;
            _steps = currentSteps;
            _stepSequence = steps?.Sequence ?? -1;
        }
        else if ((angle.Sequence != _sequence || (steps?.Sequence ?? -1) != _stepSequence || value != _previousAngle || currentSteps != _steps) && elapsed - _lastCheck >= .7)
        {
            movement = value is { } currentAngle && _previousAngle is { } previousAngle && Math.Abs(currentAngle - previousAngle) >= settings.Threshold ||
                currentSteps is { } current && _steps is { } previous && current != previous;
            if (movement)
                _movements++;
            _previousAngle = value;
            _sequence = angle.Sequence;
            _steps = currentSteps;
            _stepSequence = steps?.Sequence ?? -1;
            _lastCheck = elapsed;
        }
        if (hasAngle && hasSteps)
            source = NumericSignal.Combine(angle, steps!, (a, _) => a);
        context.Output("activity", Signals.Map(source, _ => movement ? 1 : 0));
        context.Output("count", Signals.Map(source, _ => _movements));
        context.Status(new("Tracking", Count: _movements));
        if (settings.Goal > 0 && _movements >= settings.Goal)
            context.Completion(true, "Reason.MovementGoal");
    }
}

internal sealed class CycleProcessor(CycleNode settings) : ISignalProcessor, ISuspendableProcessor
{
    private readonly Random _random = new();
    private double _started, _burstStarted, _wait, _duration, _strength, _last;
    private bool _burst, _initialized, _suspended;
    public void Suspend() => _suspended = true;
    private double Between(double min, double max) => min + _random.NextDouble() * (max - min);
    private void Reset(double elapsed)
    {
        _wait = settings.Random ? Between(settings.MinimumWait, settings.MaximumWait) : settings.WaitSeconds;
        _duration = settings.Random ? Between(settings.MinimumBurst, settings.MaximumBurst) : settings.BurstSeconds;
        _strength = settings.Random ? Between(settings.MinimumStrength, settings.MaximumStrength) : 1;
        _started = elapsed;
        _burst = false;
        _initialized = true;
        _suspended = false;
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        var elapsed = context.Elapsed.TotalSeconds;
        var interrupted = _initialized && elapsed - _last > 2 || !_initialized && elapsed > 2;
        if (!_initialized)
            Reset(0);
        if (interrupted || _suspended)
            Reset(elapsed);
        _last = elapsed;
        var activity = context.Input("activity");
        if (settings.RequireActivity && !activity.IsValid(context.Now))
        {
            Reset(elapsed);
            context.Output("gate", NumericSignal.Missing);
            context.Output("trigger", NumericSignal.Constant(0));
            context.Output("remaining", NumericSignal.Constant(_wait));
            context.Status(new("Waiting"));
            return;
        }
        if (!_burst && settings.ActivityReset && activity.IsValid(context.Now) && activity.At(0) > 0)
            _started = elapsed;
        if (_burst && elapsed >= _burstStarted + _duration)
            Reset(elapsed);
        if (!_burst && elapsed - _started >= _wait)
        {
            _burst = true;
            _burstStarted = elapsed;
        }
        var remaining = Math.Max(0, _burst ? _burstStarted + _duration - elapsed : _wait - (elapsed - _started));
        var gate = new NumericSignal(Enumerable.Range(0, 4).Select(i => _burst && !interrupted && i * .025 < remaining ? _strength : 0).ToArray(), DateTimeOffset.MaxValue);
        if (settings.RequireActivity)
            gate = NumericSignal.Combine(gate, activity, (value, _) => value);
        context.Output("gate", gate);
        context.Output("trigger", NumericSignal.Constant(_burst ? 1 : 0));
        context.Output("remaining", NumericSignal.Constant(remaining));
        context.Status(new(_burst ? "Burst" : "Resting", _burst ? _strength : 0, RemainingSeconds: remaining));
    }
}

internal sealed class AutomationProcessor(StrengthSettings settings) : ISignalProcessor, IInteractiveProcessor, ISuspendableProcessor
{
    private readonly Random _random = new();
    private long _bucket;
    private double _addition;
    private bool _boost;
    private bool _reset;
    private double _epoch;
    public void Update(NodeControl control)
    {
        if (control.Action == "boost")
            _boost = control.Read<bool>();
        if (control.Action == "reset")
            _reset = true;
    }
    public void Suspend() => _boost = false;
    public void Evaluate(NodeEvaluationContext context)
    {
        var level = context.Input("value");
        var boost = context.Input("boost");
        if (_reset)
        {
            _epoch = context.Elapsed.TotalSeconds;
            _bucket = 0;
            _addition = 0;
            _boost = false;
            _reset = false;
        }
        var bucket = (long)Math.Floor((context.Elapsed.TotalSeconds - _epoch) / settings.IntervalSeconds);
        if (bucket > _bucket)
        {
            _bucket = bucket;
            _addition = _random.NextDouble() * settings.RandomAddition;
        }
        context.Output("value", NumericSignal.Combine(level, boost, (value, addition) => value <= 0 ? 0 : Math.Clamp(value + (settings.Mode switch
        {
            StrengthMode.RandomIncrease => _addition / 100,
            StrengthMode.StepIncrease => Math.Min(Math.Max(value, settings.Ceiling / 100) - value, bucket * settings.Step / 100),
            _ => 0
        }) + (_boost || addition > 0 ? settings.Boost / 100 : 0), 0, 1)));
    }
}

internal sealed class PumpTimerProcessor(PumpTimerNode settings) : ISignalProcessor, IInteractiveProcessor
{
    private PumpOutput _settings = settings.Settings;
    private double _aEpoch, _bEpoch;
    private bool _restartA, _restartB;
    public void Update(NodeControl control)
    {
        if (control.Action != "pump")
            return;
        var next = control.Read<PumpOutput>();
        next.Validate();
        _restartA = next.Direction != _settings.Direction || next.PeristalticSeconds != _settings.PeristalticSeconds;
        _restartB = next.Water != _settings.Water || next.WaterSeconds != _settings.WaterSeconds;
        _settings = next;
    }
    public void Evaluate(NodeEvaluationContext context)
    {
        var elapsed = context.Elapsed.TotalSeconds;
        if (_restartA)
        {
            _aEpoch = elapsed;
            _restartA = false;
        }
        if (_restartB)
        {
            _bEpoch = elapsed;
            _restartB = false;
        }
        var a = _settings.PeristalticSeconds == 0 ? 0 : Math.Max(0, Math.Ceiling(_settings.PeristalticSeconds - (elapsed - _aEpoch)));
        var b = _settings.WaterSeconds == 0 ? 0 : Math.Max(0, Math.Ceiling(_settings.WaterSeconds - (elapsed - _bEpoch)));
        var direction = a == 0 && _settings.PeristalticSeconds > 0 ? PumpDirection.Off : _settings.Direction;
        var water = _settings.Water && (_settings.WaterSeconds == 0 || b > 0);
        context.Output("direction", NumericSignal.Constant((int)direction));
        context.Output("water", NumericSignal.Constant(water ? 1 : 0));
        context.Output("peristalticSeconds", NumericSignal.Constant(a));
        context.Output("waterSeconds", NumericSignal.Constant(b));
        var complete = (_settings.Direction != PumpDirection.Off || _settings.Water) && direction == PumpDirection.Off && !water;
        if (settings.StopWhenComplete)
            context.Completion(complete, "Reason.PumpComplete");
        context.Status(new(complete ? "Safe" : "Tracking", RemainingSeconds: Math.Max(a, b)));
    }
}
