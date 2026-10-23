using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Moodle.Application.Pedagogy;
using Moodle.Presentation.Tools.Pedagogy;

namespace Moodle.Application.Tests.Tools.Pedagogy;

public sealed class MoodlePedagogyToolsTests
{
    [Fact]
    public async Task Consulta_delega_e_retorna_campos_estruturados()
    {
        var search = new FakeSearch();
        var sut = new MoodlePedagogyTools(search);

        var result = await sut.SearchAsync("avaliacao formativa", 3);

        Assert.False(result.IsError ?? false);
        Assert.Equal("avaliacao formativa", search.Query);
        Assert.Equal(3, search.Limit);
        var item = Assert.Single(Assert.IsType<JsonElement>(result.StructuredContent).GetProperty("data").GetProperty("results").EnumerateArray());
        Assert.Equal("guia.md", item.GetProperty("relativePath").GetString());
        Assert.Equal("Guia", item.GetProperty("title").GetString());
        Assert.Equal("Feedback", item.GetProperty("section").GetString());
        Assert.Equal("Trecho", item.GetProperty("excerpt").GetString());
        Assert.Equal(9, item.GetProperty("score").GetInt32());
    }

    [Fact]
    public void Descricao_ordena_consulta_nos_contextos_pedagogicos()
    {
        var method = typeof(MoodlePedagogyTools).GetMethod(nameof(MoodlePedagogyTools.SearchAsync))!;
        var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description.ToLowerInvariant();

        foreach (var term in new[] { "avalia", "feedback", "planejamento", "fóruns", "acompanhamento", "relatórios" })
            Assert.Contains(term, description);
    }

    private sealed class FakeSearch : IPedagogicGuidanceSearch
    {
        public string? Query { get; private set; }
        public int Limit { get; private set; }

        public Task<IReadOnlyList<PedagogicGuidanceSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            Query = query; Limit = limit;
            return Task.FromResult<IReadOnlyList<PedagogicGuidanceSearchResult>>([new("Guia", "Feedback", "guia.md", "Trecho", 9)]);
        }
    }
}
