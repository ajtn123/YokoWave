namespace YokoWave.Core.Bluetooth;

internal static class BluetoothProtocolDriverFactory
{
    public static BluetoothProtocolDriver Create(string id, BluetoothDeviceProfile profile, IBluetoothConnection link,
        Action<DeviceStatusUpdate> publish, Action<Exception> fault) => profile.Protocol switch
    {
        BluetoothProtocol.YokonexV1 or BluetoothProtocol.YokonexV2 => new YokonexElectricalDriver(id, profile, link, publish, fault),
        BluetoothProtocol.YokonexMotor => new YokonexMotorDriver(id, profile, link, publish, fault),
        BluetoothProtocol.YokonexPump => new YokonexPumpDriver(id, profile, link, publish, fault),
        BluetoothProtocol.CoyoteV2 => new CoyoteV2Driver(id, profile, link, publish, fault),
        BluetoothProtocol.CoyoteV3 => new CoyoteV3Driver(id, profile, link, publish, fault),
        BluetoothProtocol.Opossum => new OpossumDriver(id, profile, link, publish, fault),
        BluetoothProtocol.Paw => new PawDriver(id, profile, link, publish, fault),
        BluetoothProtocol.Civet => new CivetDriver(id, profile, link, publish, fault),
        _ => throw new Localization.LocalizedOperationException("Error.ModeDeviceMismatch")
    };
}
