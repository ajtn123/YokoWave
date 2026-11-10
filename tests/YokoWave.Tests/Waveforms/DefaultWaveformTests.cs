using System.Security.Cryptography;
using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Bluetooth;
using YokoWave.Core.Localization;

namespace YokoWave.Tests.Waveforms;

public sealed class DefaultWaveformTests
{
    [Theory]
    [InlineData("yokonex-waveforms", 22, 1379, .1)]
    [InlineData("coyote-waveforms", 24, 3052, .025)]
    [InlineData("ovc-waveforms", 20, 1780, .025)]
    public void BuiltInResourcesUseTheSameFormatAsUserImports(string resource, int count, int samples, double interval)
    {
        string json = ReadResource(resource);
        var imported = WaveformExchange.Import(json);
        Assert.Equal(count, imported.Count);
        Assert.Equal(samples, imported.Sum(wave => wave.Samples!.Length));
        Assert.All(imported, wave =>
        {
            Assert.Equivalent(DefaultWaveformCatalog.Waveforms.Single(builtIn => builtIn.Id == wave.Id), wave, strict: true);
            Assert.Equal(wave.Samples!.Length * interval, wave.PeriodSeconds, 8);
        });
        using var definitions = JsonDocument.Parse(json);
        var localizer = new JsonLocalizer("en");
        foreach (var definition in definitions.RootElement.EnumerateArray())
        {
            string nameKey = definition.GetProperty("NameKey").GetString()!;
            Assert.Contains(nameKey, localizer.English.Keys);
            Assert.Contains(nameKey, localizer.Chinese.Keys);
        }
    }

    [Fact]
    public void CatalogContainsAllSystemPatternsAndTheirFullSamples()
    {
        var waveforms = DefaultWaveformCatalog.Waveforms;
        Assert.Equal(66, waveforms.Count);
        Assert.Equal(6211, waveforms.Sum(p => p.Samples!.Length));
        Assert.Equal(66, waveforms.Select(p => p.Id).Distinct().Count());
        Assert.All(waveforms, waveform =>
        {
            waveform.Validate();
            Assert.Equal(WaveShape.Sampled, waveform.Shape);
        });
        Assert.True(Assert.IsAssignableFrom<ICollection<WaveformAsset>>(waveforms).IsReadOnly);
        Assert.Equivalent(waveforms, WaveformExchange.Import(WaveformExchange.Export(waveforms)), strict: true);
    }

    [Theory]
    [InlineData("coyote-waveforms", "1b171f464ccbf4897de1010fd481d2773b5311934e69116c935391a2a39a6026")]
    [InlineData("ovc-waveforms", "1d330c583dab78917d15b6c7f5bd5f12fbf5016418c191ae1abdec936f829e7d")]
    public void NormalizedKitWaveformsRenderEveryFrameBackToTheUpstreamBytes(string resource, string expectedHash)
    {
        var waveforms = WaveformExchange.Import(ReadResource(resource));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var waveform in waveforms)
        {
            for (int frame = 0; frame < waveform.Samples!.Length / 4; frame++)
            {
                var output = new OutputFrame(100, 0, 20, OutputLimit: 100,
                    WindowA: WaveformGenerator.Window(waveform, frame / 10d));
                hash.AppendData(DgLabProtocol.V3(output).AsSpan(4, 8));
            }
        }
        Assert.Equal(expectedHash, Convert.ToHexStringLower(hash.GetHashAndReset()));
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

    private static string ReadResource(string resource)
    {
        using var stream = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream($"YokoWave.Core.Data.{resource}.json");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
