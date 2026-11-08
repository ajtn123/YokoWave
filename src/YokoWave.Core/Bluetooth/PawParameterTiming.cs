namespace YokoWave.Core.Bluetooth;

/// <summary>The ramp byte encodes seconds, then minutes; zero disables continuous parameter changes.</summary>
public static class PawParameterTiming
{
    public static TimeSpan? Duration(int code)
    {
        ProtocolGuard.Byte(code, 0, 120);
        return code == 0 ? null : code <= 60 ? TimeSpan.FromSeconds(code) : TimeSpan.FromMinutes(code - 60);
    }
}
