using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;

namespace YokoWave.Remote.DgLab.V4;

/// <summary>Each wire frame represents 100 ms; normalized samples remain independent of the wire version.</summary>
public static class DgLabV4Pulse
{
    public static WaveformSample[][] Decode(JsonElement value, int version)
    {
        if (version is not (2 or 3) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 500)
            throw new DgLabV4Exception("invalid_operate");
        return value.EnumerateArray().Select(frame => DecodeFrame(Bytes(frame, version == 2 ? 3 : 8), version)).ToArray();
    }

    public static string Encode(OutputFrame frame, int channel, int version = 3)
    {
        if (channel is < 0 or > 1)
            throw new DgLabV4Exception("invalid_operate");
        if (version == 2)
            return Convert.ToHexString(DgLabProtocol.V2Pulse(frame, channel == 1));
        if (version != 3 || channel is < 0 or > 1)
            throw new DgLabV4Exception("invalid_operate");
        return Convert.ToHexString(DgLabProtocol.V3(frame).AsSpan(channel == 0 ? 4 : 12, 8));
    }

    private static byte[] Bytes(JsonElement frame, int length)
    {
        try
        {
            byte[] data = frame.ValueKind == JsonValueKind.String ? Convert.FromHexString(frame.GetString()!)
                : frame.ValueKind == JsonValueKind.Array ? frame.EnumerateArray().Select(b => b.GetByte()).ToArray() : [];
            if (data.Length == length)
                return data;
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException) { }
        throw new DgLabV4Exception("invalid_operate");
    }

    private static WaveformSample[] DecodeFrame(byte[] data, int version)
    {
        if (version == 2)
        {
            var pulse = DgLabProtocol.ParseV2Pulse(data) ?? throw new DgLabV4Exception("invalid_operate");
            if (pulse.X + pulse.Y == 0 && pulse.Z == 0)
                return Enumerable.Repeat(new WaveformSample(1, 20, 0), 4).ToArray();
            if (pulse.Z > 20 || pulse.X + pulse.Y is < 10 or > 1000)
                throw new DgLabV4Exception("invalid_operate");
            return Enumerable.Repeat(new WaveformSample(1, 1000d / (pulse.X + pulse.Y), pulse.Z / 20d), 4).ToArray();
        }
        if (data.Take(4).Any(b => b is < 10 or > 240) || data.Skip(4).Any(b => b > 100))
            throw new DgLabV4Exception("invalid_operate");
        return Enumerable.Range(0, 4).Select(index =>
        {
            int encoded = data[index];
            double period = encoded <= 100 ? encoded : encoded <= 200 ? 100 + (encoded - 100) * 5 : 600 + (encoded - 200) * 10;
            return new WaveformSample(1, 1000 / period, data[index + 4] / 100d);
        }).ToArray();
    }
}
