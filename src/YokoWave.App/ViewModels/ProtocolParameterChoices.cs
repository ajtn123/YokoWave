using YokoWave.Core.Bluetooth;

namespace YokoWave.App.ViewModels;

internal static class ProtocolParameterChoices
{
    public static IReadOnlyList<SessionOption> RampDurations() => Enumerable.Range(0, 121).Select(code =>
    {
        var duration = PawParameterTiming.Duration(code);
        return duration is null ? new SessionOption(code, "Protocol.DurationDisabled")
            : code <= 60 ? new SessionOption(code, "Protocol.DurationSeconds", (int)duration.Value.TotalSeconds)
                : new SessionOption(code, "Protocol.DurationMinutes", (int)duration.Value.TotalMinutes);
    }).ToArray();

    public static IReadOnlyList<SessionOption> PawColors(int minimum, int maximum) =>
        Enumerable.Range(minimum, maximum - minimum + 1).Select(code => new SessionOption(code, $"ProtocolColor.{code}")).ToArray();
}
