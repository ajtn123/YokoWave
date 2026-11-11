namespace YokoWave.Core.Bluetooth;

/// <summary>Merges partial advertisements before identifying profiles and resolving missing names.</summary>
public sealed class BluetoothDiscoveryCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BluetoothPeripheral> _peripherals = new(StringComparer.Ordinal);

    public void Add(BluetoothPeripheral peripheral)
    {
        lock (_gate)
        {
            if (_peripherals.TryGetValue(peripheral.Id, out var previous))
            {
                peripheral = peripheral with
                {
                    Name = string.IsNullOrWhiteSpace(peripheral.Name) ? previous.Name : peripheral.Name.Trim(),
                    Signal = peripheral.Signal ?? previous.Signal,
                    Services = (previous.Services ?? []).Concat(peripheral.Services ?? []).Distinct().ToArray(),
                    AddressKind = peripheral.AddressKind == BluetoothAddressKind.Unspecified ? previous.AddressKind : peripheral.AddressKind,
                    IsConnectable = previous.IsConnectable || peripheral.IsConnectable
                };
            }
            _peripherals[peripheral.Id] = peripheral;
        }
    }

    public IReadOnlyList<BluetoothPeripheral> Supported()
    {
        lock (_gate)
            return _peripherals.Values.Where(peripheral => BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services) is not null)
                .OrderByDescending(peripheral => peripheral.Signal).ToArray();
    }

    public async Task ResolveNamesAsync(Func<BluetoothPeripheral, CancellationToken, Task<string?>> resolve, CancellationToken cancellationToken)
    {
        BluetoothPeripheral[] pending;
        lock (_gate)
            pending = _peripherals.Values.Where(peripheral => peripheral.IsConnectable && string.IsNullOrWhiteSpace(peripheral.Name) &&
                BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services) is null).OrderByDescending(peripheral => peripheral.Signal).ToArray();
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (peripheral, token) =>
        {
            string? name = await resolve(peripheral, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(name))
                Add(peripheral with { Name = name });
        }).ConfigureAwait(false);
    }
}
