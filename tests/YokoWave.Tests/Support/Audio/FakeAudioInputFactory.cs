using YokoWave.Core;

namespace YokoWave.Tests.Support.Audio;

internal sealed class FakeAudioInputFactory(TimeProvider? clock = null) : IAudioInputFactory
{
    public bool SupportsSystemAudio => true;
    public List<AudioSettings> Requests { get; } = [];
    public List<FakeAudioInputSession> Sessions { get; } = [];
    public bool BlockStart { get; set; }
    public Exception? StartError { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IAudioInputSession> StartAsync(AudioSettings settings, CancellationToken cancellationToken)
    {
        Requests.Add(settings);
        Entered.TrySetResult();
        if (BlockStart)
            await Task.Delay(Timeout.Infinite, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (StartError is { } error)
            throw error;
        var session = new FakeAudioInputSession(clock ?? TimeProvider.System);
        Sessions.Add(session);
        return session;
    }
}
