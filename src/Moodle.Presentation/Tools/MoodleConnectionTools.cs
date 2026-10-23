using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleConnectionTools(IMoodleConnectionCatalog connectionCatalog)
{
    public const string ListConnectionsToolName = "moodle_list_connections";

    [McpServerTool(
        Name = ListConnectionsToolName,
        Title = "List Moodle Connections",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolResponse<IReadOnlyList<MoodleAvailableConnection>>))]
    [MoodleToolMetadata(
        Family = "infrastructure",
        Classification = "R1",
        Kind = "read",
        CanonicalOperation = "connector.connections.list",
        Structural = true,
        ExposureStatus = "Keep",
        ExposureReason = "Permite escolher explicitamente uma conexão própria antes de consultar dados Moodle.",
        Evidence = "Lista somente aliases e conexão padrão das conexões ativas do usuário autenticado.",
        RequiredPlatformPermission = "tool.connections.manage")]
    [Description("Lista os aliases das conexões Moodle ativas da sua conta e identifica a conexão padrão. Se o usuário mencionar uma instituição ou conexão, chame esta ferramenta, escolha o alias correspondente e informe esse moodleAlias em todas as ferramentas Moodle seguintes. Não use o nome da instituição como termo de busca de cursos quando ele indicar a conexão.")]
    public async Task<CallToolResult> ListConnectionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connections = (await connectionCatalog.GetActiveConnectionsAsync(cancellationToken))
                .Select(connection => new MoodleAvailableConnection(connection.Alias, connection.IsDefault))
                .ToArray();
            var message = connections.Length == 0
                ? "Nenhuma conexão Moodle ativa está disponível para esta conta."
                : $"Encontrei {connections.Length} conexão(ões) Moodle ativa(s). Use o alias escolhido nas consultas seguintes.";
            var response = new ToolResponse<IReadOnlyList<MoodleAvailableConnection>>(
                "ok",
                connections,
                [],
                AuditId: null,
                DateTimeOffset.UtcNow,
                Message: message);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = message }],
                StructuredContent = JsonSerializer.SerializeToElement(response),
                IsError = false
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolResultHelper.Error<IReadOnlyList<MoodleAvailableConnection>>(exception);
        }
    }
}

public sealed record MoodleAvailableConnection(
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("isDefault")] bool IsDefault);
