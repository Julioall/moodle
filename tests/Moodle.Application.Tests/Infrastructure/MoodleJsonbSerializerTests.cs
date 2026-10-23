using System.Text.Json;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleJsonbSerializerTests
{
    [Fact]
    public void Serialize_RemoveNulESequenciasSurrogateInvalidasSemQuebrarOJson()
    {
        var invalid = "antes\0depois" + char.ConvertFromUtf32(0x1F4DA) + "-" + '\ud800';

        var result = MoodleJsonbSerializer.Serialize(
            new { text = invalid, nested = new[] { invalid } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(result.SanitizedCharacters >= 2);
        using var document = JsonDocument.Parse(result.Json);
        var text = document.RootElement.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.DoesNotContain('\0', text!);
        Assert.DoesNotContain('\ud800', text!);
        Assert.Contains("antes", text!, StringComparison.Ordinal);
        Assert.Contains("depois", text!, StringComparison.Ordinal);
        Assert.Contains("📚", text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_PreservaPayloadValidoSemContarSanitizacao()
    {
        var result = MoodleJsonbSerializer.Serialize(
            new { state = "published", count = 2 },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(0, result.SanitizedCharacters);
        Assert.Equal("published", JsonDocument.Parse(result.Json).RootElement.GetProperty("state").GetString());
    }
}
