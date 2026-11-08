namespace YokoWave.Core;

internal sealed class ExerciseModeRuntime(SessionProgram program, WaveformPreset preset, TimeProvider clock, Random random)
    : ActivityModeRuntime(program, preset, clock, random)
{
    private readonly ReactiveSettings _settings = program.Reactive ?? new();
    protected override bool NeedsMotion => true;
    // APK movement threshold: sensitivity 0..100 maps linearly from 50.5° down to 0.2°.
    protected override double MovementThreshold => 50.5 - .503 * _settings.MovementSensitivity;
    protected override BurstPlan NextCycle() => new(_settings.IdleSeconds, _settings.BurstSeconds);
    public override string? CompletionReason(int targetCount) => _settings.MovementGoalEnabled && AllReachedGoal(targetCount, _settings.MovementGoal)
        ? "Reason.MovementGoal" : null;
}
