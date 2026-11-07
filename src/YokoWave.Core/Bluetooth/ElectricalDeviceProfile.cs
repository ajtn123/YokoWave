namespace YokoWave.Core.Bluetooth;

public enum ElectricalGeneration { LegacyV1, CombinedV2 }

public sealed record ElectricalDeviceProfile(string Name, ElectricalGeneration Generation)
{
    public static ElectricalDeviceProfile? FromName(string name) => name.Trim().ToUpperInvariant() switch
    {
        "YYC-DJ" => new("YYC-DJ", ElectricalGeneration.LegacyV1),
        "YCY-DJ" => new("YCY-DJ", ElectricalGeneration.CombinedV2),
        "YYC-DJV2" => new("YYC-DJV2", ElectricalGeneration.CombinedV2),
        "YYC-DJ-V2" => new("YYC-DJ-V2", ElectricalGeneration.CombinedV2),
        _ => null
    };

    public byte[] Stop() => Generation == ElectricalGeneration.LegacyV1 ? V1Protocol.Stop() : V2Protocol.Stop();
    public IReadOnlyList<byte[]> Encode(OutputFrame frame) => Generation == ElectricalGeneration.LegacyV1
        ? V1Protocol.Encode(frame) : [V2Protocol.Encode(frame)];
}
