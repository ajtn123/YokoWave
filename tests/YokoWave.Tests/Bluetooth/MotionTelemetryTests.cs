using YokoWave.Core.Bluetooth;

namespace YokoWave.Tests.Bluetooth;

public sealed class MotionTelemetryTests
{
    [Fact]
    public void MotionPacketsValidateChecksumSignedAccelerationAndZeroVectors()
    {
        byte[] data = [0x35, 0x71, 6, 0xC0, 0, 0, 0, 0x40, 0, 0, 0, 0, 0, 0, 0];
        byte[] packet = V1Protocol.WithChecksum(data);
        Assert.Equal(45, V1Protocol.Parse(packet)!.MotionAngle!.Value, 8);
        packet[^1] ^= 1;
        Assert.Null(V1Protocol.Parse(packet));
        var zero = V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(zero!.MotionAngle);
        Assert.Equal(new SensorAxes(0, 0, 0), zero.AccelerationAxes);
        Assert.Equal(1234, V1Protocol.Parse(V1Protocol.WithChecksum([0x35, 0x71, 5, 4, 0xD2]))!.Steps);
        Assert.Equal(new byte[] { 0x35, 0x14, 1, 0x4A }, V1Protocol.MotionStreaming(true));
        Assert.Equal(new byte[] { 0x35, 0x14, 0, 0x49 }, V1Protocol.MotionStreaming(false));
    }
}
