using YokoWave.Core.Bluetooth;

namespace YokoWave.Tests.Bluetooth;

public sealed class BluetoothDiscoveryTests
{
    [Fact]
    public void PartialAdvertisementsPreserveNameServicesAddressKindAndConnectability()
    {
        var catalog = new BluetoothDiscoveryCatalog();
        var service = BluetoothDeviceProfiles.Standard(0x180c);
        var battery = BluetoothDeviceProfiles.Standard(0x180a);
        catalog.Add(new("ble:1234", "", -50, [service], AddressKind: BluetoothAddressKind.Random));
        Assert.Empty(catalog.Supported());
        catalog.Add(new("ble:1234", "47L121000", -45, [battery], IsConnectable: false));
        catalog.Add(new("ble:1234", "", -40, [service], IsConnectable: false));

        var device = Assert.Single(catalog.Supported());
        Assert.Equal("47L121000", device.Name);
        Assert.Equal(-40, device.Signal);
        Assert.Equal(BluetoothAddressKind.Random, device.AddressKind);
        Assert.True(device.IsConnectable);
        Assert.Equal(2, device.Services!.Count);
        Assert.Contains(service, device.Services);
        Assert.Contains(battery, device.Services);
        Assert.Equal(BluetoothProtocol.CoyoteV3, BluetoothDeviceProfiles.Identify(device.Name, device.Services)!.Protocol);
    }

    [Fact]
    public async Task MissingNamesAreResolvedWithoutProbingKnownNamedOrNonconnectableDevices()
    {
        var catalog = new BluetoothDiscoveryCatalog();
        catalog.Add(new("hidden", "", -40, AddressKind: BluetoothAddressKind.Random));
        catalog.Add(new("named", "47L121000", -50));
        catalog.Add(new("keyboard", "Keyboard", -30));
        catalog.Add(new("beacon", "", -20, IsConnectable: false));
        catalog.Add(new("motor", "", -60, [BluetoothDeviceProfiles.Standard(0xff40)]));
        List<string> queried = [];
        await catalog.ResolveNamesAsync((device, _) =>
        {
            queried.Add(device.Id);
            Assert.Equal(BluetoothAddressKind.Random, device.AddressKind);
            return Task.FromResult<string?>("47L121000");
        }, TestContext.Current.CancellationToken);

        Assert.Equal("hidden", Assert.Single(queried));
        Assert.Equal(3, catalog.Supported().Count);
        Assert.Equal("47L121000", catalog.Supported().Single(device => device.Id == "hidden").Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Unrelated device")]
    public async Task SharedDgLabServicesDoNotGuessTheModelWhenNameResolutionFails(string? name)
    {
        var catalog = new BluetoothDiscoveryCatalog();
        catalog.Add(new("unknown", "", -40, [BluetoothDeviceProfiles.Standard(0x180c)]));
        await catalog.ResolveNamesAsync((_, _) => Task.FromResult(name), TestContext.Current.CancellationToken);
        Assert.Empty(catalog.Supported());
    }

    [Fact]
    public async Task CancellationWaitsForNameLookupsAndPreservesPreviouslyIdentifiedDevices()
    {
        var catalog = new BluetoothDiscoveryCatalog();
        catalog.Add(new("known", "47L121000", -45));
        catalog.Add(new("unknown", "", -40));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool released = false;
        var lookup = catalog.ResolveNamesAsync(async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "47L121000";
            }
            finally
            {
                released = true;
            }
        }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.True(released);
        Assert.Equal("known", Assert.Single(catalog.Supported()).Id);
    }
}
