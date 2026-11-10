using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YokoWave.Core;

namespace YokoWave.Tests.Waveforms;

public sealed class WaveformSchemaTests
{
    [Fact]
    public void SchemasAcceptAllBuiltInsExportedShapesAndEmptyStorageWithoutNetworkResolution()
    {
        var (single, collection) = Schemas();
        foreach (string resource in typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceNames().Where(name => name.StartsWith("YokoWave.Core.Data.", StringComparison.Ordinal)))
        {
            using var stream = typeof(DefaultWaveformCatalog).Assembly.GetManifestResourceStream(resource);
            using var document = JsonDocument.Parse(stream!);
            Assert.True(collection.Evaluate(document.RootElement).IsValid);
            Assert.All(document.RootElement.EnumerateArray(), wave => Assert.True(single.Evaluate(wave).IsValid));
        }

        var generated = Enum.GetValues<WaveShape>().Where(shape => shape != WaveShape.Sampled).Select(shape => new WaveformAsset(Guid.NewGuid(), shape.ToString(), shape, 1, 20, shape == WaveShape.Custom ? [0, 1] : null));
        using var exported = JsonDocument.Parse(WaveformExchange.Export(DefaultWaveformCatalog.Waveforms.Concat(generated)));
        Assert.True(collection.Evaluate(exported.RootElement).IsValid);
        Assert.All(exported.RootElement.EnumerateArray(), wave => Assert.False(wave.TryGetProperty("ChannelCount", out _)));
        using var empty = JsonDocument.Parse("[]");
        Assert.True(collection.Evaluate(empty.RootElement).IsValid);
    }

    [Theory]
    [InlineData(336, true)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void SchemaAndAssetValidationAgreeOnExtendedSampleCounts(int count, bool valid)
    {
        var wave = DefaultWaveformCatalog.Waveforms[0] with { Samples = Enumerable.Repeat(new WaveformSample(1, 20, .5), count).ToArray() };
        var (single, _) = Schemas();
        using var exported = JsonDocument.Parse(WaveformExchange.Export([wave]));
        Assert.Equal(valid, wave.TryValidate(out _));
        Assert.Equal(valid, single.Evaluate(exported.RootElement[0]).IsValid);
        if (valid)
            Assert.Equal(count, Assert.Single(WaveformExchange.Import(exported.RootElement.GetRawText())).Samples!.Length);
        else
            Assert.ThrowsAny<ArgumentException>(() => WaveformExchange.Import(exported.RootElement.GetRawText()));
    }

    [Theory]
    [InlineData("FormatVersion", "2")]
    [InlineData("Id", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("Id", "\"invalid\"")]
    [InlineData("Name", "\"   \"")]
    [InlineData("PeriodSeconds", "0.1")]
    [InlineData("Frequency", "101")]
    [InlineData("Shape", "\"Custom\"")]
    [InlineData("Samples", "null")]
    [InlineData("Samples.0.Level", "1.1")]
    [InlineData("Samples.0.FrequencyHz", "0")]
    [InlineData("Samples.0.PulseWidth", "null")]
    [InlineData("Samples.0.Channels", "[]")]
    [InlineData("Samples.0.Channels", "[1,0,0,1]")]
    [InlineData("Samples.0.Channels", "[-1.1]")]
    [InlineData("Samples.0.FrequencyCode", "20")]
    public void SchemaRejectsUnsupportedVersionsRangesMissingDataAndProtocolFields(string path, string json)
    {
        var (single, _) = Schemas();
        var root = JsonNode.Parse(WaveformExchange.Export([DefaultWaveformCatalog.Waveforms[0]]))!.AsArray()[0]!;
        string[] pieces = path.Split('.');
        var target = root;
        foreach (string piece in pieces[..^1])
            target = int.TryParse(piece, out int index) ? target[index]! : target[piece]!;
        target[pieces[^1]] = JsonNode.Parse(json);
        Assert.False(single.Evaluate(JsonSerializer.SerializeToElement(root)).IsValid);
    }

    private static (JsonSchema Single, JsonSchema Collection) Schemas()
    {
        string folder = Path.Combine(TestPaths.RepositoryRoot, "schemas");
        var options = new BuildOptions
        {
            SchemaRegistry = new()
        };
        using var single = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "waveform.schema.json")));
        using var collection = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "waveforms.schema.json")));
        return (JsonSchema.Build(single.RootElement.Clone(), options), JsonSchema.Build(collection.RootElement.Clone(), options));
    }
}
