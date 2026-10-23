using System.Text.Json.Nodes;
using Moodle.Application.Registry;
using Moodle.Domain.Registry;

namespace Moodle.Application.Tests.Registry;

public sealed class ResponseNormalizerTests
{
    [Fact]
    public void Normalize_TruncatesLargeArraysWithPaginationMetadata()
    {
        var raw = new JsonArray(Enumerable.Range(1, 10).Select(value => JsonValue.Create(value)).ToArray());

        var result = Assert.IsType<JsonObject>(new ResponseNormalizer().Normalize(
            "generic-read",
            raw,
            new NormalizationContext(NormalizationMode.Agent, MaxItems: 3, MaxPayloadBytes: 4096)));

        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.True(result["hasMore"]!.GetValue<bool>());
        Assert.Equal(3, result["returned"]!.GetValue<int>());
        Assert.Equal(10, result["total"]!.GetValue<int>());
    }

    [Fact]
    public void Normalize_DoesNotTruncateShadowResponses()
    {
        var raw = new JsonArray(Enumerable.Range(1, 10).Select(value => JsonValue.Create(value)).ToArray());

        var result = Assert.IsType<JsonArray>(new ResponseNormalizer().Normalize(
            "generic-read",
            raw,
            new NormalizationContext(NormalizationMode.Shadow, MaxItems: 1, MaxPayloadBytes: 1)));

        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void Normalize_ProducesBoundedPreviewForLargeObjectsInAgentMode()
    {
        var raw = new JsonObject
        {
            ["payload"] = new string('x', 5000)
        };

        var result = Assert.IsType<JsonObject>(new ResponseNormalizer().Normalize(
            "generic-read",
            raw,
            new NormalizationContext(NormalizationMode.Agent, MaxItems: 10, MaxPayloadBytes: 256)));

        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.True(result["hasMore"]!.GetValue<bool>());
        Assert.Equal(256, result["maxPayloadBytes"]!.GetValue<long>());
        Assert.False(string.IsNullOrWhiteSpace(result["preview"]!.GetValue<string>()));
    }
}
