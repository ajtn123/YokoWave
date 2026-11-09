using System.Text.Json;
using Xunit;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Tests;

public sealed class DefaultWaveformTests
{
    [Fact]
    public void CatalogContainsAllSystemPatternsAndTheirFullSamples()
    {
        var waveforms = DefaultWaveformCatalog.Waveforms;
        Assert.Equal(24, waveforms.Count);
        Assert.Equal(1379, waveforms.Take(22).Sum(p => p.Samples!.Length));
        Assert.Equal(140, waveforms.Skip(22).Sum(p => p.Samples!.Length));
        Assert.Equal(24, waveforms.Select(p => p.Id).Distinct().Count());
        Assert.All(waveforms, waveform =>
        {
            waveform.Validate();
            Assert.Equal(WaveShape.Sampled, waveform.Shape);
            Assert.Equal(waveform.Samples!.Length * (waveforms.ToList().IndexOf(waveform) >= 22 ? .025 : .1), waveform.PeriodSeconds, 8);
        });
        Assert.True(Assert.IsAssignableFrom<ICollection<WaveformAsset>>(waveforms).IsReadOnly);

        using var resource = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream("YokoWave.Core.Data.default-waveforms.json");
        using var definitions = JsonDocument.Parse(resource!);
        var localizer = new JsonLocalizer("en");
        foreach (var definition in definitions.RootElement.EnumerateArray())
        {
            string nameKey = definition.GetProperty("NameKey").GetString()!;
            Assert.Contains(nameKey, localizer.English.Keys);
            Assert.Contains(nameKey, localizer.Chinese.Keys);
        }
        Assert.Equivalent(waveforms, WaveformExchange.Import(WaveformExchange.Export(waveforms)), strict: true);
    }

    [Fact]
    public void SampleSequenceKeepsStepBoundariesFrequencyPulseWidthAndMotorDirection()
    {
        var tide = DefaultWaveformCatalog.Waveforms.Single(p => p.Id == Guid.Parse("d7470000-0000-4000-8001-000000004857"));
        Assert.Equal(0.01, WaveformGenerator.Sample(tide, 0.05));
        Assert.Equal(0.18, WaveformGenerator.Sample(tide, 0.1));
        Assert.Equal(98, WaveformGenerator.ReferenceSample(tide, 0.1)!.FrequencyHz);
        Assert.Equal(.18, WaveformGenerator.ReferenceSample(tide, 0.1)!.PulseWidth);
        Assert.Equal(WaveformGenerator.Sample(tide, 0.1), WaveformGenerator.Sample(tide, tide.PeriodSeconds + 0.1));
        var motor = DefaultWaveformCatalog.Waveforms.Single(p => p.Id == Guid.Parse("d7470000-0000-4000-8002-000000031309"));
        Assert.Equal(new double[] { -.25, 1, 1 }, motor.Samples![0].Channels);
        Assert.Equal(0.85, WaveformGenerator.Sample(motor, 0.1));
        Assert.Equal(new double[] { -.25, .75, .85 }, WaveformGenerator.ReferenceSample(motor, 0.1)!.Channels);
    }

    [Fact]
    public void ImportAcceptsLegacyDescriptionButExportDoesNotIncludeIt()
    {
        string json = """
        [{"Id":"9e191a92-a04e-4cc0-8e3d-205bf2135284","Name":"Legacy","Shape":"Custom","PeriodSeconds":2,"Frequency":20,"Description":"Ignored description","FormatVersion":1,"Points":[0,1,0]}]
        """;
        var waveform = Assert.Single(WaveformExchange.Import(json));
        Assert.Equal("Legacy", waveform.Name);
        string exported = WaveformExchange.Export([waveform]);
        using var document = JsonDocument.Parse(exported);
        Assert.False(document.RootElement[0].TryGetProperty("Description", out _));
    }
}
