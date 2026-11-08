namespace YokoWave.Core.Bluetooth;

public enum BluetoothProtocol { YokonexV1, YokonexV2, YokonexMotor, YokonexPump, CoyoteV2, CoyoteV3, Opossum, Paw, Civet, UnsupportedPaw }
public enum OutputDomain { Electrical, Motor, Pump, Input }
public sealed record GattAddress(Guid Service, Guid Characteristic);
public sealed record GattEndpoint(GattAddress Address, bool Write = false, bool Notify = false, bool Read = false, bool Required = true);
public sealed record GattNotification(GattAddress Address, byte[] Data);

public sealed record BluetoothDeviceProfile(BluetoothProtocol Protocol, string NameKey, OutputDomain Domain,
    int Channels, IReadOnlyList<GattEndpoint> Endpoints, DeviceCapability Capabilities = DeviceCapability.None, bool Supported = true)
{
    public bool Supports(DeviceCapability capability) => (Capabilities & capability) == capability;
    public bool Motion => Supports(DeviceCapability.Motion);
    public bool Contacts => Supports(DeviceCapability.Contacts);
    public bool Light => Supports(DeviceCapability.Light);
    public IReadOnlyList<DeviceQuery> SupportedQueries => Protocol switch
    {
        BluetoothProtocol.YokonexV1 => [DeviceQuery.All, DeviceQuery.ChannelA, DeviceQuery.ChannelB, DeviceQuery.AuxiliaryMotor, DeviceQuery.Battery, DeviceQuery.Steps, DeviceQuery.Motion],
        BluetoothProtocol.YokonexV2 => [DeviceQuery.All, DeviceQuery.ChannelA, DeviceQuery.ChannelB, DeviceQuery.AuxiliaryMotor, DeviceQuery.Battery, DeviceQuery.Steps, DeviceQuery.Motion, DeviceQuery.Firmware, DeviceQuery.Light],
        BluetoothProtocol.YokonexMotor => [DeviceQuery.All, DeviceQuery.Product],
        BluetoothProtocol.CoyoteV2 => [DeviceQuery.All, DeviceQuery.Battery, DeviceQuery.ChannelA, DeviceQuery.ChannelB],
        BluetoothProtocol.YokonexPump or BluetoothProtocol.CoyoteV3 or BluetoothProtocol.Opossum or BluetoothProtocol.Civet => [DeviceQuery.All, DeviceQuery.Battery],
        _ => []
    };
    public GattAddress Write => Endpoints.First(endpoint => endpoint.Write).Address;
    public GattAddress Notify => Endpoints.First(endpoint => endpoint.Notify).Address;
    public bool Accepts(WaveformPreset preset) => Supported && Domain is OutputDomain.Electrical or OutputDomain.Motor && preset.TryValidate(out _);
}

public static class BluetoothDeviceProfiles
{
    public static Guid Standard(ushort value) => Guid.Parse($"0000{value:x4}-0000-1000-8000-00805f9b34fb");
    public static Guid Coyote2(ushort value) => Guid.Parse($"955a{value:x4}-0fe2-f5aa-a094-84b8d4f3e8ad");
    public static GattAddress Address(ushort service, ushort characteristic) => new(Standard(service), Standard(characteristic));
    public static readonly GattAddress DgWrite = Address(0x180c, 0x150a), DgNotify = Address(0x180c, 0x150b), DgBattery = Address(0x180a, 0x1500);
    public static readonly GattAddress V2Strength = new(Coyote2(0x180b), Coyote2(0x1504));
    // The published channel descriptions map B to PWM_A34 (1505), A to PWM_B34 (1506).
    public static readonly GattAddress V2WaveA = new(Coyote2(0x180b), Coyote2(0x1506)), V2WaveB = new(Coyote2(0x180b), Coyote2(0x1505));
    public static readonly GattAddress V2Battery = new(Coyote2(0x180a), Coyote2(0x1500));

    private static GattEndpoint[] Yokonex(ushort service) => [new(Address(service, (ushort)(service + 1)), Write: true), new(Address(service, (ushort)(service + 2)), Notify: true)];
    private static GattEndpoint[] Dg(bool battery = true) => battery
        ? [new(DgWrite, Write: true), new(DgNotify, Notify: true), new(DgBattery, Notify: true, Read: true, Required: false)]
        : [new(DgWrite, Write: true), new(DgNotify, Notify: true)];
    public static IReadOnlyList<BluetoothDeviceProfile> All
    { get; } =
    [
        new(BluetoothProtocol.YokonexV1, "Hardware.YokonexV1", OutputDomain.Electrical, 2, Yokonex(0xff30), ElectricalCapabilities),
        new(BluetoothProtocol.YokonexV2, "Hardware.YokonexV2", OutputDomain.Electrical, 2, Yokonex(0xff30), ElectricalCapabilities | DeviceCapability.Light | DeviceCapability.BufferedElectrical),
        new(BluetoothProtocol.YokonexMotor, "Hardware.YokonexMotor", OutputDomain.Motor, 3, Yokonex(0xff40), DeviceCapability.StatusQuery | DeviceCapability.MotorPresets),
        new(BluetoothProtocol.YokonexPump, "Hardware.YokonexPump", OutputDomain.Pump, 2, Yokonex(0xffb0), DeviceCapability.StatusQuery | DeviceCapability.TimedPump),
        new(BluetoothProtocol.CoyoteV2, "Hardware.CoyoteV2", OutputDomain.Electrical, 2,
            [new(V2Strength, Write: true, Notify: true, Read: true), new(V2WaveA, Write: true, Read: true), new(V2WaveB, Write: true, Read: true), new(V2Battery, Read: true, Notify: true, Required: false)], DeviceCapability.StatusQuery | DeviceCapability.RawPulse),
        new(BluetoothProtocol.CoyoteV3, "Hardware.CoyoteV3", OutputDomain.Electrical, 2, Dg(), DeviceCapability.StatusQuery | DeviceCapability.RelativeStrength | DeviceCapability.ChannelLimits | DeviceCapability.Balance),
        new(BluetoothProtocol.Opossum, "Hardware.Opossum", OutputDomain.Motor, 2, Dg(), DeviceCapability.StatusQuery | DeviceCapability.SensorStream | DeviceCapability.DisplaySync),
        new(BluetoothProtocol.Paw, "Hardware.Paw", OutputDomain.Input, 0, Dg(false), DeviceCapability.SensorStream | DeviceCapability.IndicatorColor | DeviceCapability.PawTriggers | DeviceCapability.PawCalibration | DeviceCapability.ShoulderLight),
        new(BluetoothProtocol.Civet, "Hardware.Civet", OutputDomain.Input, 0, Dg(), DeviceCapability.StatusQuery | DeviceCapability.SensorStream | DeviceCapability.PressureReset | DeviceCapability.ScreenOrientation),
        new(BluetoothProtocol.UnsupportedPaw, "Hardware.PawOld", OutputDomain.Input, 0, Dg(false), Supported: false)
    ];
    private const DeviceCapability ElectricalCapabilities = DeviceCapability.StatusQuery | DeviceCapability.Motion | DeviceCapability.Contacts |
        DeviceCapability.AuxiliaryMotor | DeviceCapability.StepCounter | DeviceCapability.ElectricalPresets;
    public static BluetoothDeviceProfile Get(BluetoothProtocol protocol) => All.First(profile => profile.Protocol == protocol);
    public static BluetoothDeviceProfile? FromId(string? id) => Enum.TryParse<BluetoothProtocol>(id, out var protocol) && Enum.IsDefined(protocol) ? Get(protocol) : null;
    public static BluetoothDeviceProfile? Identify(string name, IReadOnlyCollection<Guid>? services = null)
    {
        var value = name.Trim().ToUpperInvariant();
        var protocol = value switch
        {
            "YYC-DJ" => BluetoothProtocol.YokonexV1,
            "YCY-DJ" or "YYC-DJV2" or "YYC-DJ-V2" => BluetoothProtocol.YokonexV2,
            "D-LAB ESTIM01" => BluetoothProtocol.CoyoteV2,
            "47L120100" => BluetoothProtocol.UnsupportedPaw,
            _ when value.StartsWith("47L121", StringComparison.Ordinal) => BluetoothProtocol.CoyoteV3,
            _ when value.StartsWith("47L127", StringComparison.Ordinal) => BluetoothProtocol.Opossum,
            _ when value.StartsWith("47L1203", StringComparison.Ordinal) => BluetoothProtocol.Paw,
            _ when value.StartsWith("47L124", StringComparison.Ordinal) => BluetoothProtocol.Civet,
            _ when services?.Contains(Standard(0xff40)) == true => BluetoothProtocol.YokonexMotor,
            _ when services?.Contains(Standard(0xffb0)) == true => BluetoothProtocol.YokonexPump,
            _ => (BluetoothProtocol?)null
        };
        return protocol is { } found ? Get(found) : null;
    }
}
