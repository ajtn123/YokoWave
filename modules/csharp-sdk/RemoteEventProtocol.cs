using System.Text.Json.Serialization;

namespace YokoWave.Sdk;

public enum RemoteEventKind { Trigger, Number }

public sealed record RemoteEventDefinition([property: JsonRequired] string Id, [property: JsonRequired] RemoteEventKind Kind,
    string? Name = null, double Minimum = -1_000_000, double Maximum = 1_000_000, int ValidityMilliseconds = 1000)
{
    internal bool IsValid => RemoteEventProtocol.ValidKey(Id) && Enum.IsDefined(Kind) &&
        (Name is null || !string.IsNullOrWhiteSpace(Name) && Name.Length <= 120) &&
        double.IsFinite(Minimum) && double.IsFinite(Maximum) && Minimum >= -1e12 && Maximum <= 1e12 && Minimum <= Maximum &&
        ValidityMilliseconds is >= 100 and <= 10000;
}

public sealed record RemoteEventRegistration(string ClientId, RemoteEventDefinition[] Events, bool Simulated = false);
public sealed record RemoteEventDescriptor(string ClientId, RemoteEventDefinition Event);
public sealed record RemoteEventReceipt(long Sequence);

internal static class RemoteEventProtocol
{
    public const int Capacity = 256;
    public static bool ValidKey(string? value) => value is { Length: > 0 and <= 80 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    public static void Validate(RemoteEventRegistration registration)
    {
        if (registration is null || !ValidKey(registration.ClientId) || registration.Events is not { Length: > 0 and <= 128 } ||
            registration.Events.Any(item => item is null || !item.IsValid) || registration.Events.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != registration.Events.Length)
            throw new RemoteRpcException(-32602, "Error.RemoteEvents");
    }
}
