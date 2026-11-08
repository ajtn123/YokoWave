using YokoWave.Core.Localization;

namespace YokoWave.Core;

public enum ModeKind { Waveform, Touch, Audio, Restraint, Exercise, SleepDeprivation, Pump }
public enum AudioSourceKind { Microphone, MediaFile, SystemAudio }
public enum AudioFollowMode { Volume, Rhythm }
public enum SleepCycleKind { Fixed, Random, ActivityReset }

public sealed record AudioSettings(AudioSourceKind Source = AudioSourceKind.Microphone, string? MediaPath = null,
    double Sensitivity = 50, AudioFollowMode Follow = AudioFollowMode.Volume, double NoiseFloorDb = -70,
    double PlaybackVolume = 60, bool LoopMedia = false)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Source) || !Enum.IsDefined(Follow) ||
            !double.IsFinite(Sensitivity) || Sensitivity is < 0 or > 100 ||
            !double.IsFinite(NoiseFloorDb) || NoiseFloorDb is < -90 or > -30 ||
            !double.IsFinite(PlaybackVolume) || PlaybackVolume is < 0 or > 100 || MediaPath?.Length > 4096)
            throw new LocalizedArgumentException("Error.AudioSettings");
    }
}

public sealed record ReactiveSettings(double SafeAngle = 5, double RampAngle = 30, double MovementSensitivity = 80,
    double IdleSeconds = 10, double BurstSeconds = 5, bool MovementGoalEnabled = false, int MovementGoal = 500,
    SleepCycleKind SleepCycle = SleepCycleKind.Fixed, double IntervalSeconds = 90,
    double MinimumIntervalSeconds = 30, double MaximumIntervalSeconds = 90,
    double MinimumBurstSeconds = 5, double MaximumBurstSeconds = 10,
    double MinimumStrengthPercent = 50, double MaximumStrengthPercent = 100)
{
    public void Validate()
    {
        var values = new[] { SafeAngle, RampAngle, MovementSensitivity, IdleSeconds, BurstSeconds, IntervalSeconds,
            MinimumIntervalSeconds, MaximumIntervalSeconds, MinimumBurstSeconds, MaximumBurstSeconds, MinimumStrengthPercent, MaximumStrengthPercent };
        if (values.Any(value => !double.IsFinite(value)) || SafeAngle is < 0 or > 90 || RampAngle is < 1 or > 180 ||
            MovementSensitivity is < 0 or > 100 || IdleSeconds is < 1 or > 3600 || BurstSeconds is < 1 or > 60 ||
            MovementGoal is < 1 or > 1000000 || !Enum.IsDefined(SleepCycle) || IntervalSeconds is < 1 or > 7200 ||
            MinimumIntervalSeconds is < 1 or > 7200 || MaximumIntervalSeconds < MinimumIntervalSeconds || MaximumIntervalSeconds > 7200 ||
            MinimumBurstSeconds is < 1 or > 60 || MaximumBurstSeconds < MinimumBurstSeconds || MaximumBurstSeconds > 60 ||
            MinimumStrengthPercent is < 0 or > 100 || MaximumStrengthPercent < MinimumStrengthPercent || MaximumStrengthPercent > 100)
            throw new LocalizedArgumentException("Error.ReactiveSettings");
    }
}
