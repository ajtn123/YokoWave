using System.Net;
using YokoWave.Core.Localization;

namespace YokoWave.Remote.DgLab.V4;

public sealed record DgLabV4RelaySettings(string Address = "127.0.0.1", int Port = 9998, string Path = "/v4",
    int HeartbeatMilliseconds = 30_000, int IdleMilliseconds = 300_000, string? CertificatePath = null,
    int PingMilliseconds = 10_000, int MaximumMissedPongs = 3)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(Address, out _) || Port is < 0 or > 65535 || Path.Length is < 1 or > 128 || !Path.StartsWith('/') ||
            Path.Contains('?') || Path.Contains('#') || Path.Length > 1 && Path.EndsWith('/') ||
            HeartbeatMilliseconds is < 100 or > 300_000 || IdleMilliseconds is < 100 or > 3_600_000 || CertificatePath?.Length > 4096 ||
            PingMilliseconds is < 100 or > 300_000 || MaximumMissedPongs is < 1 or > 30)
        {
            throw new LocalizedArgumentException("Error.RemoteSettings");
        }
    }
}
