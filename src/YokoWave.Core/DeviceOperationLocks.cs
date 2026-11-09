using System.Collections.Concurrent;

namespace YokoWave.Core;

/// <summary>Locks survive reconnects so queued work for one device cannot overlap its replacement connection.</summary>
internal sealed class DeviceOperationLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public async Task<IDisposable> AcquireAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        List<SemaphoreSlim> acquired = [];
        try
        {
            foreach (string? id in ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var gate = _gates.GetOrAdd(id, static _ => new(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired.Add(gate);
            }
            return new Lease(acquired);
        }
        catch
        {
            Release(acquired);
            throw;
        }
    }

    private static void Release(List<SemaphoreSlim> gates)
    {
        for (int index = gates.Count - 1; index >= 0; index--)
            gates[index].Release();
    }

    private sealed class Lease(List<SemaphoreSlim> gates) : IDisposable
    {
        private List<SemaphoreSlim>? _gates = gates;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _gates, null) is { } held)
                Release(held);
        }
    }
}
