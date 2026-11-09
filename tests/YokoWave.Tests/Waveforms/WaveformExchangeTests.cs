using System.Text.Json;
using YokoWave.Core;
using YokoWave.Core.Localization;

namespace YokoWave.Tests.Waveforms;

public sealed class WaveformExchangeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void ImportRejectsCountsOutsideTheFileLimit(int count)
    {
        var waveforms = Enumerable.Range(0, count).Select(_ => WaveformAsset.BuiltIn[0] with { Id = Guid.NewGuid() });
        var error = Assert.Throws<LocalizedArgumentException>(() => WaveformExchange.Import(WaveformExchange.Export(waveforms)));
        Assert.Equal("Error.ImportCount", error.LocalizedMessage.Key);
    }

    [Fact]
    public void ImportAcceptsTheMaximumNumberOfDistinctWaveforms()
    {
        var waveforms = Enumerable.Range(0, 100).Select(_ => WaveformAsset.BuiltIn[0] with { Id = Guid.NewGuid() }).ToArray();
        var restored = WaveformExchange.Import(WaveformExchange.Export(waveforms));
        Assert.Equal(waveforms.Select(waveform => waveform.Id), restored.Select(waveform => waveform.Id));
    }

    [Fact]
    public void ImportRejectsNullEntries()
    {
        var error = Assert.Throws<LocalizedArgumentException>(() => WaveformExchange.Import("[null]"));
        Assert.Equal("Error.InvalidWave", error.LocalizedMessage.Key);
    }

    [Fact]
    public void ImportRejectsOversizedPayloadBeforeParsingJson()
    {
        var error = Assert.Throws<LocalizedArgumentException>(() => WaveformExchange.Import(new string(' ', 1_000_001)));
        Assert.Equal("Error.FileSize", error.LocalizedMessage.Key);
    }

    [Fact]
    public void WaveImportRejectsInvalidRangesAndDuplicateIdentifiers()
    {
        var waveform = WaveformAsset.BuiltIn[0];
        Assert.ThrowsAny<ArgumentException>(() => WaveformExchange.Import(WaveformExchange.Export([waveform, waveform])));
        Assert.ThrowsAny<ArgumentException>(() => WaveformExchange.Import(WaveformExchange.Export([waveform with { PeriodSeconds = 0 }])));
        Assert.ThrowsAny<ArgumentException>(() => WaveformExchange.Import(WaveformExchange.Export([waveform with { Shape = WaveShape.Custom, Points = [0, 2] }])));
        Assert.Throws<JsonException>(() => WaveformExchange.Import("not json"));
        Assert.Single(WaveformExchange.Import(WaveformExchange.Export([waveform])));
    }

    [Fact]
    public void NormalizedJsonRoundTripsSignedChannelsWithoutAnyProtocolFields()
    {
        string json = WaveformExchange.Export(DefaultWaveformCatalog.Waveforms);
        string[] excludedFields = ["Family", "FrequencyCode", "PulseWidthCode", "Motors", "IsValid", "PreviewLevel"];
        foreach (string field in excludedFields)
            Assert.DoesNotContain($"\"{field}\"", json);

        Assert.Equivalent(DefaultWaveformCatalog.Waveforms, WaveformExchange.Import(json), strict: true);
        var signed = DefaultWaveformCatalog.Waveforms.SelectMany(wave => wave.Samples!).First(sample => sample.Channels?.Any(value => value < 0) == true);
        Assert.Contains(signed.Channels!, value => value < 0);
        using var document = JsonDocument.Parse(json);
        Assert.All(document.RootElement.EnumerateArray(), wave => Assert.Equal(1, wave.GetProperty("FormatVersion").GetInt32()));
    }

    [Theory]
    [InlineData("[{\"Id\":\"78bbc211-e75c-40cd-b250-08c0fedb3e02\",\"Name\":\"Old\",\"Shape\":\"Sampled\",\"PeriodSeconds\":1,\"Frequency\":20,\"Samples\":[{\"Level\":1,\"FrequencyCode\":20,\"PulseWidthCode\":100}]}]")]
    [InlineData("[{\"FormatVersion\":1,\"Id\":\"78bbc211-e75c-40cd-b250-08c0fedb3e02\",\"Name\":\"Missing\",\"Shape\":\"Sampled\",\"PeriodSeconds\":1,\"Frequency\":20,\"Samples\":[{\"Level\":1,\"FrequencyHz\":20}]}]")]
    public void ImportRejectsProtocolEncodedOrIncompleteSamples(string json) => Assert.Throws<JsonException>(() => WaveformExchange.Import(json));

    [Fact]
    public void ImportDoesNotPersistUnknownDisplayFields()
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
