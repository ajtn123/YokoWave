using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using YokoWave.Pipelines;

namespace YokoWave.Remote;

/// <summary>Keeps event catalogs scoped to one graph's services without retaining closed workspaces.</summary>
public sealed class RemoteEventRuntime
{
    private static readonly ConditionalWeakTable<PipelineServices, RemoteEventRuntime> _instances = new();
    private readonly ConcurrentDictionary<Guid, RemoteEventHub> _hubs = new();
    public static RemoteEventRuntime For(PipelineServices services) => _instances.GetValue(services, _ => new());
    public RemoteEventHub Source(Guid nodeId) => _hubs.GetOrAdd(nodeId, _ => new());
}
