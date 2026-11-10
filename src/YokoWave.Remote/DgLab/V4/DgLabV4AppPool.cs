using System.Runtime.CompilerServices;
using YokoWave.Pipelines;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>Run-scoped App connections. A failed registration still owns a reference that must be released.</summary>
internal sealed class DgLabV4AppPool
{
    private static readonly ConditionalWeakTable<PipelineServices, DgLabV4AppPool> _scopes = new();
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Owner, Uri Address), Entry> _entries = [];

    public static DgLabV4AppPool For(PipelineServices services) => _scopes.GetValue(services, _ => new());

    public Lease Acquire(NodeRunContext context, DgLabV4AppNode settings)
    {
        var key = (context.OwnerId, DgLabV4Protocol.AppUri(settings.Url, settings.ControllerId));
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new(new(context.Devices, settings.Url, settings.ControllerId));
                _entries.Add(key, entry);
            }
            entry.Acquire();
            return new(entry.Session, () => ReleaseAsync(key, entry));
        }
    }

    private async ValueTask ReleaseAsync((Guid Owner, Uri Address) key, Entry entry)
    {
        lock (_gate)
        {
            if (!entry.Release())
                return;
            _entries.Remove(key);
        }
        await entry.Session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Entry(DgLabV4AppSession session)
    {
        private int _references;
        public DgLabV4AppSession Session { get; } = session;
        public void Acquire() => _references++;
        public bool Release() => --_references == 0;
    }

    internal sealed class Lease(DgLabV4AppSession session, Func<ValueTask> release) : IAsyncDisposable
    {
        private int _disposed;
        public DgLabV4AppSession Session { get; } = session;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? release() : ValueTask.CompletedTask;
    }
}
