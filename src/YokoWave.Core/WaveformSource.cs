using System.Text.Json.Serialization;

namespace YokoWave.Core;

/// <summary>Original identity and provenance, independent of editable names and playback parameters.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WaveformSource(
    [property: JsonRequired] string Provider, [property: JsonRequired] string Collection, string? Key = null,
    string? Repository = null, string? Revision = null, string? Path = null, string? License = null)
{
    internal bool IsValid => Text(Provider, 80) && Text(Collection, 80) && (Key is null || Text(Key, 256)) &&
        (Repository is null || Text(Repository, 2048) && Uri.TryCreate(Repository, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") &&
        (Revision is null || Text(Revision, 128)) && (Path is null || Text(Path, 512)) && (License is null || Text(License, 128));

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}
