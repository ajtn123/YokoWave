#if WINDOWS
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Desktop.Bluetooth;

/// <summary>The connection builder retains ownership of resolved services, including on discovery failure.</summary>
internal sealed class WindowsGattResolver(BluetoothLEDevice device, List<GattDeviceService> ownedServices)
{
    public async Task<Dictionary<GattAddress, GattCharacteristic>> ResolveAsync(BluetoothDeviceProfile profile, CancellationToken cancellationToken)
    {
        Dictionary<GattAddress, GattCharacteristic> attributes = [];
        foreach (var group in profile.Endpoints.GroupBy(endpoint => endpoint.Address.Service))
        {
            var service = await ResolveServiceAsync(group.Key, group.Any(endpoint => endpoint.Required), cancellationToken);
            if (service is null)
                continue;
            foreach (var endpoint in group)
            {
                if (await ResolveCharacteristicAsync(service, endpoint, cancellationToken) is { } characteristic)
                    attributes[endpoint.Address] = characteristic;
            }
        }
        return attributes;
    }

    private async Task<GattDeviceService?> ResolveServiceAsync(Guid uuid, bool required, CancellationToken cancellationToken)
    {
        var result = await device.GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            foreach (var service in result.Services)
                service.Dispose();
            if (required)
                throw new LocalizedOperationException("Error.BluetoothService");
            return null;
        }
        var selected = result.Services[0];
        ownedServices.Add(selected);
        foreach (var extra in result.Services.Skip(1))
            extra.Dispose();
        return selected;
    }

    private static async Task<GattCharacteristic?> ResolveCharacteristicAsync(GattDeviceService service, GattEndpoint endpoint, CancellationToken cancellationToken)
    {
        var result = await service.GetCharacteristicsForUuidAsync(endpoint.Address.Characteristic, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        var characteristic = result.Characteristics.FirstOrDefault();
        var properties = characteristic?.CharacteristicProperties ?? GattCharacteristicProperties.None;
        bool valid = result.Status == GattCommunicationStatus.Success && characteristic is not null &&
            (!endpoint.Write || (properties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) != 0) &&
            (!endpoint.Read || properties.HasFlag(GattCharacteristicProperties.Read)) &&
            (!endpoint.Notify || properties.HasFlag(GattCharacteristicProperties.Notify) || !endpoint.Required);
        if (valid)
            return characteristic;
        if (endpoint.Required)
            throw new LocalizedOperationException("Error.BluetoothService");
        return null;
    }
}
#endif
