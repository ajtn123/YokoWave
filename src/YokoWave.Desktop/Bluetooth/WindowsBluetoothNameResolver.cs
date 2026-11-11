#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Desktop.Bluetooth;

/// <summary>Reads only the standard GAP name; never initializes an output driver.</summary>
internal sealed class WindowsBluetoothNameResolver(Action<string> diagnostic)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Id, BluetoothAddressKind Kind), CachedName> _cache = [];

    public async Task<string?> ResolveAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken)
    {
        var key = (peripheral.Id, peripheral.AddressKind);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && Stopwatch.GetElapsedTime(cached.CapturedAt) < TimeSpan.FromSeconds(cached.Name is null ? 30 : 600))
                return cached.Name;
        }
        if (!ulong.TryParse(peripheral.Id.AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        BluetoothLEDevice? device = null;
        List<GattDeviceService> ownedServices = [];
        List<Exception> errors = [];
        string? name = null;
        try
        {
            device = peripheral.AddressKind switch
            {
                BluetoothAddressKind.Random => await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Random).AsTask(timeout.Token),
                BluetoothAddressKind.Public => await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(timeout.Token),
                _ => await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(timeout.Token)
            };
            if (device is not null)
            {
                name = ActualName(device.Name);
                if (name is null)
                    name = await ReadGapNameAsync(device, ownedServices, timeout.Token) ?? ActualName(device.Name);
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or IOException)
        {
            errors.Add(error);
        }
        finally
        {
            foreach (var service in ownedServices)
                Release(service.Dispose, errors);
            if (device is not null)
                Release(device.Dispose, errors);
        }
        foreach (var error in errors)
            diagnostic($"BLE name lookup {peripheral.Id}: {error.GetType().Name}, HRESULT=0x{error.HResult:X8}");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_cache.Count >= 256)
                _cache.Remove(_cache.MinBy(pair => pair.Value.CapturedAt).Key);
            _cache[key] = new(name, Stopwatch.GetTimestamp());
        }
        if (name is not null)
            diagnostic($"BLE resolved name: {name}; addressKind={peripheral.AddressKind}; RSSI={peripheral.Signal}");
        return name;
    }

    private static string? ActualName(string name) => string.IsNullOrWhiteSpace(name) || name.StartsWith("Bluetooth ", StringComparison.OrdinalIgnoreCase) ? null : name;

    private static async Task<string?> ReadGapNameAsync(BluetoothLEDevice device, List<GattDeviceService> ownedServices, CancellationToken cancellationToken)
    {
        var services = await device.GetGattServicesForUuidAsync(BluetoothDeviceProfiles.Standard(0x1800), BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        ownedServices.AddRange(services.Services);
        if (services.Status != GattCommunicationStatus.Success)
            return null;
        foreach (var service in services.Services)
        {
            var characteristics = await service.GetCharacteristicsForUuidAsync(BluetoothDeviceProfiles.Standard(0x2a00), BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (characteristics.Status != GattCommunicationStatus.Success)
                continue;
            foreach (var characteristic in characteristics.Characteristics)
            {
                var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                if (result.Status != GattCommunicationStatus.Success || result.Value.Length is 0 or > 248)
                    continue;
                using var reader = DataReader.FromBuffer(result.Value);
                byte[] bytes = new byte[result.Value.Length];
                reader.ReadBytes(bytes);
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
            }
        }
        return null;
    }

    private static void Release(Action release, List<Exception> errors)
    {
        try
        {
            release();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private sealed record CachedName(string? Name, long CapturedAt);
}
#endif
