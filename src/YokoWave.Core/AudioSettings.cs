using YokoWave.Core.Localization;

namespace YokoWave.Core;

public enum AudioSourceKind
{
    Microphone, MediaFile, SystemAudio
}
public enum AudioFollowMode
{
    Volume, Rhythm
}

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
