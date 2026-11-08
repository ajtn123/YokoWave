namespace YokoWave.Core.Bluetooth;

[Flags]
public enum DeviceCapability
{
    None = 0,
    StatusQuery = 1 << 0,
    Motion = 1 << 1,
    Contacts = 1 << 2,
    Light = 1 << 3,
    AuxiliaryMotor = 1 << 4,
    StepCounter = 1 << 5,
    ElectricalPresets = 1 << 6,
    BufferedElectrical = 1 << 7,
    MotorPresets = 1 << 8,
    RawPulse = 1 << 9,
    RelativeStrength = 1 << 10,
    ChannelLimits = 1 << 11,
    Balance = 1 << 12,
    SensorStream = 1 << 13,
    IndicatorColor = 1 << 14,
    PawTriggers = 1 << 15,
    PawCalibration = 1 << 16,
    ShoulderLight = 1 << 17,
    PressureReset = 1 << 18,
    ScreenOrientation = 1 << 19,
    DisplaySync = 1 << 20,
    TimedPump = 1 << 21
}
