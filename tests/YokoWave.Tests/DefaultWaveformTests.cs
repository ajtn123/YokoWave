using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;
using Xunit;

namespace YokoWave.Tests;

public sealed class DefaultWaveformTests
{
    [Fact]
    public void CatalogContainsAllSystemPatternsAndTheirFullSamples()
    {
        var presets = DefaultWaveformCatalog.Presets;
        Assert.Equal(24, presets.Count);
        Assert.Equal(1379, presets.Take(22).Sum(p => p.Samples!.Length));
        Assert.Equal(140, presets.Skip(22).Sum(p => p.Samples!.Length));
        Assert.Equal(24, presets.Select(p => p.Id).Distinct().Count());
        Assert.All(presets, preset =>
        {
            preset.Validate();
            Assert.Equal(WaveShape.Sampled, preset.Shape);
            Assert.Equal(preset.Samples!.Length * (presets.ToList().IndexOf(preset) >= 22 ? .025 : .1), preset.PeriodSeconds, 8);
        });
        Assert.True(Assert.IsAssignableFrom<ICollection<WaveformPreset>>(presets).IsReadOnly);

        using var resource = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream("YokoWave.Core.Data.default-waveforms.json");
        using var definitions = JsonDocument.Parse(resource!);
        var localizer = new JsonLocalizer("en");
        foreach (var definition in definitions.RootElement.EnumerateArray())
        {
            var nameKey = definition.GetProperty("NameKey").GetString()!;
            Assert.Contains(nameKey, localizer.English.Keys);
            Assert.Contains(nameKey, localizer.Chinese.Keys);
        }
        Assert.Equivalent(presets, WorkspaceStore.ImportPresets(WorkspaceStore.ExportPresets(presets)), strict: true);
    }

    [Fact]
    public void SampleSequenceKeepsStepBoundariesFrequencyPulseWidthAndMotorDirection()
    {
        var tide = DefaultWaveformCatalog.Presets.Single(p => p.Id == Guid.Parse("d7470000-0000-4000-8001-000000004857"));
        Assert.Equal(0.01, WaveformGenerator.Sample(tide, 0.05));
        Assert.Equal(0.18, WaveformGenerator.Sample(tide, 0.1));
        Assert.Equal(98, WaveformGenerator.ReferenceSample(tide, 0.1)!.FrequencyHz);
        Assert.Equal(.18, WaveformGenerator.ReferenceSample(tide, 0.1)!.PulseWidth);
        Assert.Equal(WaveformGenerator.Sample(tide, 0.1), WaveformGenerator.Sample(tide, tide.PeriodSeconds + 0.1));
        var motor = DefaultWaveformCatalog.Presets.Single(p => p.Id == Guid.Parse("d7470000-0000-4000-8002-000000031309"));
        Assert.Equal(new double[] { -.25, 1, 1 }, motor.Samples![0].Channels);
        Assert.Equal(0.85, WaveformGenerator.Sample(motor, 0.1));
        Assert.Equal(new double[] { -.25, .75, .85 }, WaveformGenerator.ReferenceSample(motor, 0.1)!.Channels);
    }

    [Fact]
    public void ImportAcceptsLegacyDescriptionButExportDoesNotIncludeIt()
    {
        var json = """
        [{"Id":"9e191a92-a04e-4cc0-8e3d-205bf2135284","Name":"Legacy","Shape":"Custom","PeriodSeconds":2,"Frequency":20,"Description":"Ignored description","FormatVersion":1,"Points":[0,1,0]}]
        """;
        var preset = Assert.Single(WorkspaceStore.ImportPresets(json));
        Assert.Equal("Legacy", preset.Name);
        var exported = WorkspaceStore.ExportPresets([preset]);
        using var document = JsonDocument.Parse(exported);
        Assert.False(document.RootElement[0].TryGetProperty("Description", out _));
    }
}
