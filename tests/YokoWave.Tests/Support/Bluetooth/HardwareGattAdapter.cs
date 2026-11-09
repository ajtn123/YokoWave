using YokoWave.Core.Bluetooth;

namespace YokoWave.Tests.Support.Bluetooth;

internal sealed class HardwareGattAdapter : IBluetoothAdapter
{
    public List<BluetoothPeripheral> Peripherals { get; } = [];
    public Dictionary<string, HardwareGattLink> Links { get; } = [];
    public bool FailWrites { get; set; }
    public int MaximumWriteLength { get; set; } = 20;

    public HardwareGattAdapter(params BluetoothProtocol[] protocols)
    {
        foreach (var protocol in protocols)
        {
            string name = protocol switch
            {
                BluetoothProtocol.YokonexV1 => "YYC-DJ",
                BluetoothProtocol.YokonexV2 => "YCY-DJ",
                BluetoothProtocol.CoyoteV2 => "D-LAB ESTIM01",
                BluetoothProtocol.CoyoteV3 => "47L121000",
                BluetoothProtocol.Opossum => "47L127000",
                BluetoothProtocol.Paw => "47L120300",
                BluetoothProtocol.Civet => "47L124000",
                BluetoothProtocol.UnsupportedPaw => "47L120100",
                _ => protocol.ToString()
            };
            Guid[] services = protocol switch
            {
                BluetoothProtocol.YokonexMotor => [BluetoothDeviceProfiles.Standard(0xff40)],
                BluetoothProtocol.YokonexPump => [BluetoothDeviceProfiles.Standard(0xffb0)],
                _ => []
            };
            Peripherals.Add(new(protocol.ToString(), name, -40, services));
        }
    }

    public Task<IReadOnlyList<BluetoothPeripheral>> ScanAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BluetoothPeripheral>>(Peripherals.ToArray());
    public Task<IBluetoothConnection> ConnectAsync(BluetoothPeripheral peripheral, CancellationToken cancellationToken)
    {
        var link = new HardwareGattLink(BluetoothDeviceProfiles.Identify(peripheral.Name, peripheral.Services)!)
        {
            FailWrites = FailWrites,
            MaximumWriteLength = MaximumWriteLength
        };
        Links[peripheral.Id] = link;
        return Task.FromResult<IBluetoothConnection>(link);
    }
}
