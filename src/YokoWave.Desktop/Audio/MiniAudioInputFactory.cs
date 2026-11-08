using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Desktop.Audio;

public sealed class MiniAudioInputFactory : IAudioInputFactory
{
    public bool SupportsSystemAudio => OperatingSystem.IsWindows();
    public Task<IAudioInputSession> StartAsync(AudioSettings settings, CancellationToken cancellationToken) =>
        Task.Run<IAudioInputSession>(async () =>
        {
            settings.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            if (settings.Source == AudioSourceKind.SystemAudio && !SupportsSystemAudio)
                throw new LocalizedOperationException("Error.SystemAudioPlatform");
            var session = new MiniAudioInputSession();
            var errorKey = settings.Source == AudioSourceKind.MediaFile ? "Error.AudioMediaFailed" : "Error.AudioInputFailed";
            try
            {
                session.Start(settings, cancellationToken);
                return session;
            }
            catch (Exception error)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception cleanupError)
                {
                    throw new LocalizedAggregateException(errorKey, [error, cleanupError]);
                }
                if (error is ILocalizedError or OperationCanceledException)
                    throw;
                throw new LocalizedOperationException(errorKey, error);
            }
        }, cancellationToken);
}
