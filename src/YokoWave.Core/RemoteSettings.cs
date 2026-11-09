using System.Net;
using YokoWave.Core.Localization;

namespace YokoWave.Core;

public sealed record RemoteServerSettings(string Address = "127.0.0.1", int Port = 9000, int LeaseMilliseconds = 3000,
    double ControlLimit = 60, string? CertificatePath = null)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(Address, out _) || Port is < 0 or > 65535 || LeaseMilliseconds is < 1000 or > 10000 ||
            !double.IsFinite(ControlLimit) || ControlLimit is < 0 or > 100 || CertificatePath?.Length > 4096)
            throw new LocalizedArgumentException("Error.RemoteSettings");
    }
}
public enum RemoteConnectionKind
{
    Gatt, Control
}
public sealed record RemoteEndpointSettings(Guid Id, string Name, string Url, RemoteConnectionKind Kind = RemoteConnectionKind.Gatt)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 60 || string.IsNullOrWhiteSpace(Url) || Url.Length > 2048 ||
            !Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new LocalizedArgumentException("Error.RemoteSettings");
    }
}
