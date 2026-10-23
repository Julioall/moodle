using System.Text.Json;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleParameterSerializerTests
{
    [Fact]
    public void Flatten_SerializaValoresSimplesComFormatoMoodle()
    {
        var result = MoodleParameterSerializer.Flatten(new Dictionary<string, object?>
        {
            ["courseid"] = 42,
            ["includeinactive"] = true,
            ["name"] = "UC de teste"
        });

        Assert.Equal("42", result["courseid"]);
        Assert.Equal("1", result["includeinactive"]);
        Assert.Equal("UC de teste", result["name"]);
    }

    [Fact]
    public void Flatten_SerializaArrays()
    {
        var result = MoodleParameterSerializer.Flatten(new Dictionary<string, object?>
        {
            ["courseids"] = new[] { 10, 20 }
        });

        Assert.Equal("10", result["courseids[0]"]);
        Assert.Equal("20", result["courseids[1]"]);
    }

    [Fact]
    public void Flatten_SerializaObjetosAninhados()
    {
        var result = MoodleParameterSerializer.Flatten(new Dictionary<string, object?>
        {
            ["options"] = new Dictionary<string, object?>
            {
                ["includehidden"] = false,
                ["limit"] = 25
            }
        });

        Assert.Equal("0", result["options[includehidden]"]);
        Assert.Equal("25", result["options[limit]"]);
    }

    [Fact]
    public void Flatten_SerializaArrayDeObjetosJson()
    {
        using var document = JsonDocument.Parse("""{"grades":[{"userid":100,"grade":85}]}""");
        var result = MoodleParameterSerializer.Flatten(new Dictionary<string, object?>
        {
            ["grades"] = document.RootElement.GetProperty("grades").Clone()
        });

        Assert.Equal("100", result["grades[0][userid]"]);
        Assert.Equal("85", result["grades[0][grade]"]);
    }
}
