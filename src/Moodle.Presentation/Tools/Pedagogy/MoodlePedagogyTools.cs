using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Pedagogy;
using Moodle.Application.Tools;

namespace Moodle.Presentation.Tools.Pedagogy;

[McpServerToolType]
public sealed class MoodlePedagogyTools(IPedagogicGuidanceSearch guidanceSearch)
{
    [McpServerTool(
        Name = "get_pedagogical_guidelines",
        Title = "Get Pedagogical Guidelines",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<PedagogicGuidanceResponse>))]
    [Description("Consulte obrigatoriamente antes de tarefas de avaliação, feedback, planejamento, fóruns, acompanhamento de estudantes e relatórios pedagógicos.")]
    public async Task<CallToolResult> SearchAsync(
        [Description("Conceitos centrais da orientação pedagógica procurada.")] string query,
        [Description("Quantidade máxima de resultados.")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ToolResultHelper.Error<PedagogicGuidanceResponse>("Informe uma consulta pedagógica.");

        var matches = await guidanceSearch.SearchAsync(query, limit, cancellationToken);
        var data = new PedagogicGuidanceResponse(matches.Select(item => new PedagogicGuidanceItem(
            item.RelativePath, item.Title, item.Section, item.Excerpt, item.Score)).ToArray());
        var response = new ToolResponse<PedagogicGuidanceResponse>("ok", data, [], null, DateTimeOffset.UtcNow);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(data) }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = false
        };
    }
}

public sealed record PedagogicGuidanceResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<PedagogicGuidanceItem> Results);

public sealed record PedagogicGuidanceItem(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("excerpt")] string Excerpt,
    [property: JsonPropertyName("score")] int Score);
