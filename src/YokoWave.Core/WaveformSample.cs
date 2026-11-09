using System.Text.Json.Serialization;

namespace YokoWave.Core;

public sealed record WaveformSample([property: JsonRequired] double Level, [property: JsonRequired] double FrequencyHz,
    [property: JsonRequired] double PulseWidth = 1, double[]? Channels = null)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(Level) && Level is >= 0 and <= 1 && double.IsFinite(FrequencyHz) && FrequencyHz is >= 1 and <= 100 &&
        double.IsFinite(PulseWidth) && PulseWidth is >= 0 and <= 1 &&
        (Channels is null || Channels.Length is >= 1 and <= 3 && Channels.All(value => double.IsFinite(value) && value is >= -1 and <= 1));
    [JsonIgnore] public double PreviewLevel => Level * PulseWidth * (Channels?.Select(Math.Abs).Max() ?? 1);
    public double ChannelMagnitude(int channel) => Channels is null ? 1 : channel < Channels.Length ? Math.Abs(Channels[channel]) : 0;
}
