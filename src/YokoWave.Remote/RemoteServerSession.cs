using System.Text.Json;
using YokoWave.Core;

namespace YokoWave.Remote;

/// <summary>Authenticated endpoint lifecycle shared by device exports and event inputs.</summary>
public abstract class RemoteServerSession : IAsyncDisposable
{
    public abstract Guid Id { get; }
    public abstract RemoteServerSettings Settings { get; }
    public abstract bool IsRunning { get; }
    public abstract Task Completion { get; }
    public abstract Task StartAsync(CancellationToken cancellationToken = default);
    public abstract Task SetPausedAsync(bool paused);
    public abstract Task StopAsync();
    public abstract ValueTask DisposeAsync();
    internal abstract Task<object?> DispatchAsync(RemotePeerSession peer, string method, JsonElement parameters, CancellationToken cancellationToken);
    internal abstract Task DisconnectAsync(RemotePeerSession peer);
    internal abstract void ReportFault(Exception error);
}
