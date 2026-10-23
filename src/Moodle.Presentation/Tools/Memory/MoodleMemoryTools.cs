using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Memory;
using Moodle.Application.Tools;
using System.Text.Json.Serialization;

namespace Moodle.Presentation.Tools.Memory;

[McpServerToolType]
public sealed class MoodleMemoryTools(IUserMemoryService memoryService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(
        Name = "manage_user_memory",
        Title = "Manage User Memory",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<MemoryToolResponse>))]
    [Description("Salva, lista ou remove memórias duráveis do usuário autenticado. A ação remover exclui estado interno; use action=salvar, listar ou remover. Nunca envie segredos nem dados pessoais de alunos.")]
    public async Task<CallToolResult> ManageAsync(
        [Description("Ação: salvar, listar ou remover.")] string action,
        [Description("Categoria: preferencia, caminho, correcao ou decisao.")] string? category = null,
        [Description("Chave curta e estável da memória.")] string? key = null,
        [Description("Conteúdo factual, durável e reutilizável.")] string? content = null,
        [Description("Origem: explicit ou inferred.")] string? origin = null,
        [Description("Texto para filtrar memórias na listagem.")] string? query = null,
        [Description("Alias Moodle opcional para escopo ou filtro.")] string? moodleAlias = null,
        [Description("Curso opcional dentro do alias Moodle.")] string? courseId = null,
        [Description("UUID da memória exigido para remover.")] Guid? memoryId = null,
        [Description("Máximo de memórias na listagem. Padrão: 20.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = (action ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "salvar" => new MemoryToolResponse("salvar", await SaveAsync(category, key, content, origin, moodleAlias, courseId, cancellationToken), null, null),
                "listar" => new MemoryToolResponse("listar", null, await memoryService.ListAsync(new ListUserMemoriesRequest(moodleAlias, courseId, limit, category, query), cancellationToken), null),
                "remover" => new MemoryToolResponse("remover", null, null, (await RemoveAsync(memoryId, cancellationToken)).Removed),
                _ => throw new ArgumentException("Ação inválida. Use salvar, listar ou remover.", nameof(action))
            };

            var response = new ToolResponse<MemoryToolResponse>("ok", data, [], null, DateTimeOffset.UtcNow);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(data, JsonOptions) }],
                StructuredContent = JsonSerializer.SerializeToElement(response, JsonOptions),
                IsError = false
            };
        }
        catch (ArgumentException exception)
        {
            return ToolResultHelper.Error<MemoryToolResponse>(exception.Message);
        }
    }

    private Task<UserMemoryDto> SaveAsync(string? category, string? key, string? content, string? origin,
        string? moodleAlias, string? courseId, CancellationToken cancellationToken)
    {
        Require(category, nameof(category));
        Require(key, nameof(key));
        Require(content, nameof(content));
        Require(origin, nameof(origin));
        return memoryService.SaveAsync(new SaveUserMemoryRequest(category!, key!, content!, origin!, moodleAlias, courseId), cancellationToken);
    }

    private Task<RemoveUserMemoryResult> RemoveAsync(Guid? memoryId, CancellationToken cancellationToken)
    {
        if (memoryId is null || memoryId == Guid.Empty)
            throw new ArgumentException("Informe um memoryId UUID válido para remover.", nameof(memoryId));
        return memoryService.RemoveAsync(memoryId.Value, cancellationToken);
    }

    private static void Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Informe {parameterName} para salvar.", parameterName);
    }
}

public sealed record MemoryToolResponse(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("memory")] UserMemoryDto? Memory,
    [property: JsonPropertyName("memories")] IReadOnlyList<UserMemoryDto>? Memories,
    [property: JsonPropertyName("removed")] bool? Removed);
