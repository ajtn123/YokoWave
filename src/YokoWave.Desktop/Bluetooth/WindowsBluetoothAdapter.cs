#if WINDOWS
using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Desktop.Bluetooth;

internal sealed class WindowsBluetoothAdapter : IBluetoothAdapter
{
    private readonly WindowsBluetoothNameResolver _names;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    public Action<string>? Diagnostic { get; init; }
    public WindowsBluetoothAdapter() => _names = new(message => Diagnostic?.Invoke(message));
    public async Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken);
            if (adapter is null || !adapter.IsLowEnergySupported)
                throw new LocalizedOperationException("Error.BluetoothUnavailable");
            var radio = await adapter.GetRadioAsync().AsTask(cancellationToken);
            if (radio is not null && radio.State != RadioState.On)
                throw new LocalizedOperationException("Error.BluetoothOff");
            return await new WindowsBluetoothDiscovery(_names, Diagnostic).ScanAsync(adapter.IsExtendedAdvertisingSupported, cancellationToken);
        }
        finally
        {
            _scanGate.Release();
        }
    }
    public async Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken)
    {
        var profile = BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services);
        if (profile is null || !peripheral.Id.StartsWith("ble:", StringComparison.Ordinal) ||
            !ulong.TryParse(peripheral.Id.AsSpan(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address))
        {
            throw new LocalizedOperationException("Error.BluetoothRescan");
        }

        if (!profile.Supported)
            throw new LocalizedOperationException("Error.PawFirmware");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        BluetoothLEDevice? device = null;
        GattSession? session = null;
        List<GattDeviceService> ownedServices = [];
        WindowsBluetoothConnection? connection = null;
        try
        {
            device = peripheral.AddressKind switch
            {
                BluetoothAddressKind.Random => await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Random).AsTask(token),
                BluetoothAddressKind.Public => await BluetoothLEDevice.FromBluetoothAddressAsync(address, BluetoothAddressType.Public).AsTask(token),
                _ => await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token)
            };
            if (device is null)
                throw new LocalizedOperationException("Error.BluetoothConnect");
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token);
            if (session is not null)
                session.MaintainConnection = true;
            var attributes = await new WindowsGattResolver(device, ownedServices).ResolveAsync(profile, token);
            foreach (var (endpoint, characteristic) in attributes)
                Diagnostic?.Invoke($"GATT {profile.Protocol}: service={endpoint.Service}, characteristic={endpoint.Characteristic}, properties={characteristic.CharacteristicProperties}, MTU={session?.MaxPduSize}");
            connection = new(device, ownedServices, session, profile, attributes, Diagnostic);
            await connection.StartAsync(token);
            return connection;
        }
        catch (Exception error)
        {
            List<Exception> errors = [];
            void Release(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception cleanupError)
                {
                    errors.Add(cleanupError);
                }
            }
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception cleanupError)
                {
                    errors.Add(cleanupError);
                }
            }
            else
            {
                if (session is not null)
                {
                    Release(() => session.MaintainConnection = false);
                    Release(session.Dispose);
                }
                foreach (var service in ownedServices)
                    Release(service.Dispose);
                if (device is not null)
                    Release(device.Dispose);
            }
            if (errors.Count > 0)
                throw new LocalizedAggregateException("Error.Cleanup", [error, .. errors]);
            throw;
        }
    }
}

#endif
