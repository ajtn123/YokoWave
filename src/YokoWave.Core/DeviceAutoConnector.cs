namespace YokoWave.Core;

/// <summary>
/// Reconnects remembered devices at zero output. Discovery, lifecycle serialization, and callback context belong to the caller.
/// </summary>
public sealed class DeviceAutoConnector(DeviceController controller, Func<string, Task> prepare, Action<DeviceDescriptor> connected, Action<Exception> report)
{
    private readonly HashSet<string> _remembered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Attempts, DateTimeOffset Next)> _failures = new(StringComparer.Ordinal);
    public bool Enabled { get; set; }
    public bool NeedsDiscovery => Enabled && _remembered.Any(id => !controller.Connections.ContainsKey(id));
    public string[] RememberedDevices => _remembered.Order(StringComparer.Ordinal).ToArray();

    public void Load(bool enabled, IEnumerable<string> devices)
    {
        Enabled = enabled;
        _remembered.Clear();
        _remembered.UnionWith(devices);
        _failures.Clear();
    }
    public void Remember(string id)
    {
        _remembered.Add(id);
        _failures.Remove(id);
    }
    public void Forget(string id)
    {
        _remembered.Remove(id);
        _failures.Remove(id);
    }
    public async Task ConnectAvailableAsync(IEnumerable<DeviceDescriptor> devices, CancellationToken cancellationToken = default)
    {
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enabled)
                return;
            if (!_remembered.Contains(device.Id) || !device.IsSupported || controller.Connections.ContainsKey(device.Id))
                continue;
            var previous = _failures.GetValueOrDefault(device.Id);
            if (controller.Clock.GetUtcNow() < previous.Next)
                continue;
            try
            {
                await prepare(device.Id);
                cancellationToken.ThrowIfCancellationRequested();
                if (!Enabled || !_remembered.Contains(device.Id))
                    continue;
                await controller.ConnectAsync(device, cancellationToken);
                if (cancellationToken.IsCancellationRequested || !Enabled || !_remembered.Contains(device.Id))
                {
                    await controller.DisconnectAsync(device.Id, CancellationToken.None);
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                _failures.Remove(device.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                int attempts = Math.Min(previous.Attempts + 1, 5);
                _failures[device.Id] = (attempts, controller.Clock.GetUtcNow().AddSeconds(Math.Min(60, 5 * Math.Pow(2, attempts - 1))));
                if (previous.Attempts == 0)
                    report(error);
                continue;
            }
            connected(device);
        }
    }
}
