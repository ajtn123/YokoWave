using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        foreach (var waveform in imported)
        {
            Assert.Equal(2, waveform.Names!.Count);
            Assert.Equal(waveform.Names["en"], waveform.GetName(CultureInfo.GetCultureInfo("en")));
            Assert.Equal(waveform.Names["zh-CN"], waveform.GetName(CultureInfo.GetCultureInfo("zh-CN")));
            Assert.Equal(waveform.Names["zh-CN"], waveform.Name);
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
    [InlineData("coyote-waveforms", "1b171f464ccbf4897de1010fd481d2773b5311934e69116c935391a2a39a6026", "cdacadf40c65c806e7dbfe9c12a6e32ea7a17e3f2138d5b91a6f8c8094fa71d7")]
    [InlineData("ovc-waveforms", "1d330c583dab78917d15b6c7f5bd5f12fbf5016418c191ae1abdec936f829e7d", "61bc312acc9526f17e4a9392b8f72af5c8ef26601b51830b7abeaf614c7f1587")]
    public void NormalizedKitWaveformsPreserveSourceIdentityNamesAndEveryProtocolFrame(string resource, string expectedHash, string expectedSemanticHash)
    {
        var waveforms = WaveformExchange.Import(ReadResource(resource));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var semanticHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var waveform in waveforms)
        {
            var source = Assert.IsType<WaveformSource>(waveform.Source);
            string family = resource.Replace("-waveforms", "", StringComparison.Ordinal);
            Assert.Equal("dglab-kit", source.Provider);
            Assert.Equal(family, source.Collection);
            Assert.Equal($"src/waveform/{family}.ts", source.Path);
            Assert.Equal("https://github.com/dungeonlab-open/dglab-kit", source.Repository);
            Assert.Equal("3e7ed447e387c2fa52f2c27353c27ff295b0bdeb", source.Revision);
            Assert.Equal("GPL-3.0", source.License);
            semanticHash.AppendData(Encoding.UTF8.GetBytes($"{source.Key}\n{waveform.Names!["zh-CN"]}\n{waveform.Names["en"]}\n{waveform.Samples!.Length / 4}\n"));
            for (int frame = 0; frame < waveform.Samples!.Length / 4; frame++)
            {
                var output = new OutputFrame(100, 0, 20, OutputLimit: 100,
                    WindowA: WaveformGenerator.Window(waveform, frame / 10d));
                byte[] packet = DgLabProtocol.V3(output);
                hash.AppendData(packet.AsSpan(4, 8));
                semanticHash.AppendData(packet.AsSpan(4, 8));
            }
        }
        Assert.Equal(expectedHash, Convert.ToHexStringLower(hash.GetHashAndReset()));
        Assert.Equal(expectedSemanticHash, Convert.ToHexStringLower(semanticHash.GetHashAndReset()));
    }

    [Fact]
    public void NamesUseAssetLanguagesAndParentCulturesWithoutDependingOnAppLocaleKeys()
    {
        var waveform = DefaultWaveformCatalog.Waveforms.First(wave => wave.Source?.Key == "EXTRUSTION");
        Assert.Equal("Extrusion", waveform.GetName(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("挤压", waveform.GetName(CultureInfo.GetCultureInfo("zh-CN")));
        Assert.Equal(waveform.Name, waveform.GetName(CultureInfo.GetCultureInfo("ja-JP")));
        Assert.DoesNotContain(L10n.Current.English.Keys, key => key.StartsWith("DefaultWave.", StringComparison.Ordinal));
        Assert.DoesNotContain(L10n.Current.Chinese.Keys, key => key.StartsWith("DefaultWave.", StringComparison.Ordinal));
        Assert.All(DefaultWaveformCatalog.Waveforms.Where(wave => wave.Source!.Provider == "yokonex"), wave =>
        {
            Assert.Equal(wave.Id.ToString()[24..].TrimStart('0'), wave.Source!.Key);
            Assert.Contains(wave.Source.Collection, new[] { "electrical", "motor-f1" });
        });
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
