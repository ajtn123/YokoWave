#if WINDOWS
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Desktop.Bluetooth;

internal sealed class WindowsBluetoothDiscovery(WindowsBluetoothNameResolver names, Action<string>? diagnostic)
{
    public async Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(bool extendedAdvertisements, CancellationToken cancellationToken)
    {
        var catalog = new BluetoothDiscoveryCatalog();
        var diagnosticPackets = new ConcurrentDictionary<(ulong Address, string Name, string Services), byte>();
        int received = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active, AllowExtendedAdvertisements = extendedAdvertisements };
        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs packet)
        {
            Interlocked.Increment(ref received);
            string name = packet.Advertisement.LocalName?.Trim() ?? "";
            var services = packet.Advertisement.ServiceUuids.ToArray();
            var kind = packet.BluetoothAddressType switch
            {
                BluetoothAddressType.Random => BluetoothAddressKind.Random,
                BluetoothAddressType.Public => BluetoothAddressKind.Public,
                _ => BluetoothAddressKind.Unspecified
            };
            catalog.Add(new($"ble:{packet.BluetoothAddress:X12}", name, packet.RawSignalStrengthInDBm, services, AddressKind: kind, IsConnectable: packet.IsConnectable));
            if (diagnostic is not null && (name.Length > 0 || services.Length > 0) && diagnosticPackets.TryAdd((packet.BluetoothAddress, name, string.Join(",", services)), 0))
                diagnostic($"BLE advertisement: name={name}; services={string.Join(",", services)}; type={packet.AdvertisementType}; RSSI={packet.RawSignalStrengthInDBm}");
        }
        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs error)
        {
            if (error.Error == BluetoothError.Success)
                stopped.TrySetResult();
            else
            {
                var failure = new LocalizedOperationException("Error.BluetoothScan");
                failure.Data["BluetoothError"] = error.Error;
                stopped.TrySetException(failure);
            }
        }
        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        try
        {
            watcher.Start();
            await await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(8), cancellationToken), stopped.Task);
        }
        finally
        {
            watcher.Received -= OnReceived;
            watcher.Stopped -= OnStopped;
            watcher.Stop();
        }
        using var nameTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        nameTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await catalog.ResolveNamesAsync(names.ResolveAsync, nameTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        var found = catalog.Supported();
        diagnostic?.Invoke($"BLE advertisements received: {received}; supported-profile candidates: {found.Count}");
        return found;
    }
}
#endif
