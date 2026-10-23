using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Memory;
using Moodle.Application.Tools;

namespace Moodle.Presentation.Tools.Memory;

[McpServerToolType]
public sealed class MoodleMemoryDocumentTools(IUserMemoryDocumentService documentService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(
        Name = "save_user_memory_document",
        Title = "Save User Memory Document",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<MemoryDocumentToolResponse>))]
    [Description("Salva ou atualiza um documento duravel privado do usuario autenticado para modelos ou referencias extensas da IA. Aceita format markdown, html ou text. Nunca envie segredos nem dados pessoais de alunos.")]
    public async Task<CallToolResult> SalvarAsync(
        [Description("Chave curta e estavel do documento.")] string key,
        [Description("Titulo humano do documento.")] string title,
        [Description("Conteudo completo do documento. Use markdown quando possivel; html e aceito para modelos Moodle existentes.")] string content,
        [Description("Formato do conteudo: markdown, html ou text.")] string format,
        [Description("Origem: explicit ou inferred.")] string origin,
        [Description("Alias Moodle opcional para escopo.")] string? moodleAlias = null,
        [Description("Curso opcional dentro do alias Moodle.")] string? courseId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(new MemoryDocumentToolResponse(
                "salvar",
                await SaveAsync(key, title, content, format, origin, moodleAlias, courseId, cancellationToken),
                null,
                null));
        }
        catch (ArgumentException exception)
        {
            return ToolResultHelper.Error<MemoryDocumentToolResponse>(exception.Message);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<MemoryDocumentToolResponse>(exception);
        }
    }

    [McpServerTool(
        Name = "list_user_memory_documents",
        Title = "List User Memory Documents",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<MemoryDocumentToolResponse>))]
    [Description("Lista documentos duraveis privados do usuario autenticado, com filtros opcionais de texto e escopo.")]
    public async Task<CallToolResult> ListarAsync(
        [Description("Texto para filtrar documentos na listagem.")] string? query = null,
        [Description("Alias Moodle opcional para filtro.")] string? moodleAlias = null,
        [Description("Curso opcional dentro do alias Moodle.")] string? courseId = null,
        [Description("Maximo de documentos na listagem. Padrao: 20.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(new MemoryDocumentToolResponse(
                "listar",
                null,
                await documentService.ListAsync(new ListUserMemoryDocumentsRequest(moodleAlias, courseId, limit, query), cancellationToken),
                null));
        }
        catch (ArgumentException exception)
        {
            return ToolResultHelper.Error<MemoryDocumentToolResponse>(exception.Message);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<MemoryDocumentToolResponse>(exception);
        }
    }

    [McpServerTool(
        Name = "read_user_memory_document",
        Title = "Read User Memory Document",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<MemoryDocumentToolResponse>))]
    [Description("Le o conteudo completo de um documento duravel privado do usuario autenticado.")]
    public async Task<CallToolResult> LerAsync(
        [Description("UUID do documento.")] Guid documentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(new MemoryDocumentToolResponse("ler", await ReadAsync(documentId, cancellationToken), null, null));
        }
        catch (ArgumentException exception)
        {
            return ToolResultHelper.Error<MemoryDocumentToolResponse>(exception.Message);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<MemoryDocumentToolResponse>(exception);
        }
    }

    [McpServerTool(
        Name = "remove_user_memory_document",
        Title = "Remove User Memory Document",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<MemoryDocumentToolResponse>))]
    [Description("Remove um documento duravel privado do usuario autenticado e o link curto de memoria associado.")]
    public async Task<CallToolResult> RemoverAsync(
        [Description("UUID do documento.")] Guid documentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(new MemoryDocumentToolResponse("remover", null, null, (await RemoveAsync(documentId, cancellationToken)).Removed));
        }
        catch (ArgumentException exception)
        {
            return ToolResultHelper.Error<MemoryDocumentToolResponse>(exception.Message);
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<MemoryDocumentToolResponse>(exception);
        }
    }

    private Task<UserMemoryDocumentDto> SaveAsync(
        string? key,
        string? title,
        string? content,
        string? format,
        string? origin,
        string? moodleAlias,
        string? courseId,
        CancellationToken cancellationToken)
    {
        Require(key, nameof(key));
        Require(title, nameof(title));
        Require(content, nameof(content));
        Require(format, nameof(format));
        Require(origin, nameof(origin));
        return documentService.SaveAsync(new SaveUserMemoryDocumentRequest(key!, title!, content!, format!, origin!, moodleAlias, courseId), cancellationToken);
    }

    private async Task<UserMemoryDocumentDto> ReadAsync(Guid? documentId, CancellationToken cancellationToken)
    {
        var id = RequireDocumentId(documentId);
        return await documentService.ReadAsync(id, cancellationToken)
            ?? throw new ArgumentException("Documento de memoria nao encontrado.", nameof(documentId));
    }

    private Task<RemoveUserMemoryDocumentResult> RemoveAsync(Guid? documentId, CancellationToken cancellationToken)
    {
        return documentService.RemoveAsync(RequireDocumentId(documentId), cancellationToken);
    }

    private static CallToolResult Ok(MemoryDocumentToolResponse data)
    {
        var response = new ToolResponse<MemoryDocumentToolResponse>("ok", data, [], null, DateTimeOffset.UtcNow);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(data, JsonOptions) }],
            StructuredContent = JsonSerializer.SerializeToElement(response, JsonOptions),
            IsError = false
        };
    }

    private static Guid RequireDocumentId(Guid? documentId)
    {
        if (documentId is null || documentId == Guid.Empty)
            throw new ArgumentException("Informe um documentId UUID valido.", nameof(documentId));
        return documentId.Value;
    }

    private static void Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Informe {parameterName} para salvar.", parameterName);
    }
}

public sealed record MemoryDocumentToolResponse(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("document")] UserMemoryDocumentDto? Document,
    [property: JsonPropertyName("documents")] IReadOnlyList<UserMemoryDocumentDto>? Documents,
    [property: JsonPropertyName("removed")] bool? Removed);
