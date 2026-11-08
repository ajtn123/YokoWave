namespace YokoWave.Core.Bluetooth;

public enum AuxiliaryMotorMode { Off = 0, On = 1, Pattern1 = 0x11, Pattern2 = 0x12, Pattern3 = 0x13 }
public sealed record SensorAxes(int X, int Y, int Z);
public sealed record MotorDeviceInfo(int Model, int Revision, int ModesA, int ModesB, int ModesC)
{
    public int ModeCount(int channel) => channel switch { 0 => ModesA, 1 => ModesB, 2 => ModesC, _ => 0 };
}
public sealed record PawEventState(int EventId, bool? Active, int? Parameter);
