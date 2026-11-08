namespace YokoWave.Core;

public enum InputPhase { Waiting, Safe, Tracking, Resting, Burst }
public sealed record ReactiveStatus(string DeviceId, InputPhase Phase, double? Angle = null, double Deviation = 0,
    int Movements = 0, double RemainingSeconds = 0, double Level = 0);

/// <summary>Per-session reactive state. Sensor baselines and activity counters are never device configuration.</summary>
public sealed class ReactivePlayback(SessionProgram program, TimeProvider clock, Random random)
{
    private sealed class State
    {
        public double? Baseline, PreviousAngle;
        public long Sequence = -1;
        public int? Steps;
        public int Movements;
        public double CycleStart, LastMotionCheck, BurstEnd, BurstStart, LastRender;
        public bool HasRendered, Fresh, InBurst;
        public double Interval, BurstLength, StrengthFactor = 1;
        public WaveformPreset? WaveA, WaveB;
    }
    public static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(2);
    private readonly ReactiveSettings _settings = program.Reactive ?? new();
    private readonly AudioSettings _audioSettings = program.Audio ?? new();
    private readonly Dictionary<string, State> _states = [];
    private readonly Dictionary<string, ReactiveStatus> _status = [];
    private readonly AudioEnvelope _left = new(), _right = new();
    private AudioFeatureFrame? _audio;
    private long _audioSequence = -1;
    private double _leftScale, _rightScale;
    public IReadOnlyDictionary<string, ReactiveStatus> Status => _status;
    public int MovementCount => _states.Values.Select(state => state.Movements).DefaultIfEmpty().Min();
    public bool GoalReached(int targetCount) => program.Kind == ModeKind.Exercise && _settings.MovementGoalEnabled &&
        _states.Count == targetCount && _states.Values.All(state => state.Movements >= _settings.MovementGoal);
    public void SetAudio(AudioFeatureFrame? frame) => _audio = frame;
    public void Suspend()
    {
        _left.Reset(); _right.Reset(); _audio = null; _audioSequence = -1; _leftScale = _rightScale = 0;
        if (program.NeedsMotion) foreach (var state in _states.Values) state.Fresh = false;
    }
    public void ResetInput(double elapsed, bool recalibrate = false)
    {
        _left.Reset(); _right.Reset(); _audio = null; _audioSequence = -1; _leftScale = _rightScale = 0;
        foreach (var state in _states.Values)
        {
            state.CycleStart = elapsed; state.InBurst = false; state.BurstEnd = 0; state.Fresh = false;
            state.PreviousAngle = null; state.Sequence = -1;
            if (recalibrate) state.Baseline = null;
        }
    }
    private bool Fresh(DateTimeOffset at) => clock.GetUtcNow() - at is var age && age >= TimeSpan.Zero && age <= InputTimeout;
    public OutputFrame Render(string id, TimeSpan elapsedTime, OutputFrame frame, double baseA, double baseB, MotionReading? motion, double baseC = 0)
    {
        var elapsed = elapsedTime.TotalSeconds;
        if (program.Kind == ModeKind.Audio) return RenderAudio(id, frame, baseA, baseB, baseC);
        if (!_states.TryGetValue(id, out var state)) { state = new(); _states.Add(id, state); NewCycle(state, 0); }
        // Never deliver a delayed burst after a suspended or stalled UI resumes.
        var interrupted = state.HasRendered ? elapsed - state.LastRender > InputTimeout.TotalSeconds : elapsed > InputTimeout.TotalSeconds;
        state.HasRendered = true; state.LastRender = elapsed;
        if (interrupted) { NewCycle(state, elapsed); state.Fresh = false; }
        if (program.NeedsMotion)
        {
            if (motion is null || !double.IsFinite(motion.Angle) || motion.Angle is < 0 or > 180 || !Fresh(motion.CapturedAt))
            {
                state.Fresh = false; state.PreviousAngle = null; state.InBurst = false; state.CycleStart = elapsed;
                _status[id] = new(id, InputPhase.Waiting, Movements: state.Movements);
                return frame with { ChannelA = 0, ChannelB = 0, ChannelC = 0 };
            }
            if (!state.Fresh)
            {
                state.Fresh = true; state.PreviousAngle = motion.Angle; state.Sequence = motion.Sequence;
                state.Steps = motion.Steps; state.CycleStart = elapsed; state.LastMotionCheck = elapsed;
                state.Baseline ??= motion.Angle;
            }
            if (program.Kind == ModeKind.Restraint)
            {
                var deviation = Math.Abs(motion.Angle - state.Baseline!.Value);
                var factor = Math.Clamp((deviation - _settings.SafeAngle) / _settings.RampAngle, 0, 1);
                _status[id] = new(id, factor == 0 ? InputPhase.Safe : InputPhase.Tracking, motion.Angle, deviation, Level: factor);
                return ParameterFrame(frame, Math.Ceiling(baseA * factor), Math.Ceiling(baseB * factor), factor) with { ChannelC = Math.Ceiling(baseC * factor) };
            }
            if (motion.Sequence != state.Sequence && !state.InBurst && elapsed - state.LastMotionCheck >= .7)
            {
                var threshold = program.Kind == ModeKind.Exercise ? 50.5 - .503 * _settings.MovementSensitivity : 1;
                var changed = Math.Abs(motion.Angle - (state.PreviousAngle ?? motion.Angle)) >= threshold ||
                    motion.Steps is { } steps && state.Steps is { } previous && steps != previous;
                if (changed) { state.Movements++; state.CycleStart = elapsed; }
                state.PreviousAngle = motion.Angle; state.Sequence = motion.Sequence; state.Steps = motion.Steps; state.LastMotionCheck = elapsed;
            }
        }
        if (state.InBurst && elapsed >= state.BurstEnd) NewCycle(state, elapsed);
        if (!state.InBurst && elapsed - state.CycleStart >= state.Interval)
        {
            state.InBurst = true; state.BurstStart = elapsed; state.BurstEnd = elapsed + state.BurstLength;
        }
        _status[id] = new(id, state.InBurst ? InputPhase.Burst : InputPhase.Resting, motion?.Angle,
            Movements: state.Movements, RemainingSeconds: Math.Max(0, state.InBurst ? state.BurstEnd - elapsed : state.Interval - (elapsed - state.CycleStart)),
            Level: state.InBurst ? state.StrengthFactor : 0);
        if (!state.InBurst || interrupted) return frame with { ChannelA = 0, ChannelB = 0, ChannelC = 0 };
        if (program.Kind == ModeKind.SleepDeprivation && _settings.SleepCycle == SleepCycleKind.Random && state.WaveA is { } a && state.WaveB is { } b)
        {
            var time = elapsed - state.BurstStart;
            return frame with
            {
                ChannelA = baseA * state.StrengthFactor * Envelope(a, time),
                ChannelB = baseB * state.StrengthFactor * Envelope(b, time),
                ChannelC = baseC * state.StrengthFactor * Envelope(a, time),
                Frequency = a.Frequency,
                ReferenceSample = WaveformGenerator.ReferenceSample(a, time),
                ReferenceSampleB = WaveformGenerator.ReferenceSample(b, time) ?? new(1, b.Frequency, 100),
                FamilyA = a.Family, FamilyB = b.Family,
                WindowA = PlaybackCompatibility.Window(a, time), WindowB = PlaybackCompatibility.Window(b, time)
            };
        }
        return frame;
    }
    private static double Envelope(WaveformPreset wave, double time) => wave.Family == WaveformFamily.Generic ? WaveformGenerator.Sample(wave, time) : 1;
    private void NewCycle(State state, double elapsed)
    {
        state.CycleStart = elapsed; state.InBurst = false;
        state.Interval = program.Kind == ModeKind.Exercise ? _settings.IdleSeconds : _settings.IntervalSeconds;
        state.BurstLength = _settings.BurstSeconds; state.StrengthFactor = 1;
        if (program.Kind != ModeKind.SleepDeprivation || _settings.SleepCycle != SleepCycleKind.Random) return;
        state.Interval = Between(_settings.MinimumIntervalSeconds, _settings.MaximumIntervalSeconds);
        state.BurstLength = Between(_settings.MinimumBurstSeconds, _settings.MaximumBurstSeconds);
        state.StrengthFactor = Between(_settings.MinimumStrengthPercent, _settings.MaximumStrengthPercent) / 100;
        if (program.Playlist is { Count: > 0 } waves) { state.WaveA = waves[random.Next(waves.Count)]; state.WaveB = waves[random.Next(waves.Count)]; }
    }
    private double Between(double min, double max) => min + random.NextDouble() * (max - min);
    private OutputFrame RenderAudio(string id, OutputFrame frame, double baseA, double baseB, double baseC)
    {
        var audio = _audio;
        if (audio is null || !Fresh(audio.CapturedAt) || new[] { audio.LeftDb, audio.RightDb, audio.LeftOnset, audio.RightOnset }.Any(value => !double.IsFinite(value)) ||
            audio.LeftDb is < -120 or > 0 || audio.RightDb is < -120 or > 0 || audio.LeftOnset is < 0 or > 1 || audio.RightOnset is < 0 or > 1)
        {
            _left.Reset(); _right.Reset(); _leftScale = _rightScale = 0;
            _status[id] = new(id, InputPhase.Waiting);
            return frame with { ChannelA = 0, ChannelB = 0, ChannelC = 0 };
        }
        if (audio.Sequence != _audioSequence)
        {
            _audioSequence = audio.Sequence;
            _leftScale = _left.Process(audio.LeftDb, audio.LeftOnset, _audioSettings);
            _rightScale = _right.Process(audio.RightDb, audio.RightOnset, _audioSettings);
        }
        var level = Math.Max(_leftScale, _rightScale);
        _status[id] = new(id, level > 0 ? InputPhase.Tracking : InputPhase.Safe, Level: level);
        var a = ParameterFrame(frame, baseA * _leftScale, baseB * _rightScale, _leftScale);
        return a with { ReferenceSampleB = Parameters(_rightScale), ChannelC = baseC * _leftScale };
    }
    private static WaveformSample Parameters(double factor) => new(factor, Math.Max(2, 100 - Math.Ceiling(factor * 100)), Math.Ceiling(factor * 100));
    private static OutputFrame ParameterFrame(OutputFrame original, double a, double b, double factor) =>
        original with { ChannelA = a, ChannelB = b, ReferenceSample = Parameters(factor), ReferenceSampleB = Parameters(factor),
            FamilyA = WaveformFamily.Generic, FamilyB = WaveformFamily.Generic, WindowA = null, WindowB = null };
}
