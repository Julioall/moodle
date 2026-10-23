using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

[McpServerToolType]
public sealed class MoodleUniversalWriteTools(
    IMoodleUniversalWriteService writeService,
    IMoodleConnectionSelection connectionSelection)
{
    [McpServerTool(Name = "moodle_prepare_write", Title = "Preparar Escrita Moodle",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<MoodleWritePreview>))]
    [Description("Cria uma prévia de uma escrita Moodle habilitada para o token atual. Não chama o Moodle. Exige Features:UniversalMoodleWriteEnabled=true, conexão CanWrite e confirmação literal posterior.")]
    public async Task<CallToolResult> PrepareWriteAsync(
        [Description("Nome exato da função Web Service Moodle que exige confirmação de escrita.")] string functionName,
        [Description("Objeto JSON com os parâmetros da função Moodle.")] JsonElement parameters,
        [Description("Alias opcional da conexão Moodle.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return ToolResultHelper.Error<MoodleWritePreview>("Informe o nome da função Moodle.");
        }
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return ToolResultHelper.Error<MoodleWritePreview>("Os parâmetros devem ser fornecidos como objeto JSON.");
        }

        connectionSelection.Alias = moodleAlias;
        try
        {
            var data = await writeService.PrepareAsync(functionName, ToParameters(parameters), cancellationToken);
            return Success(data, $"Escrita '{data.Function}' preparada. Revise e confirme usando o texto literal informado.");
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException ex) { return ToolResultHelper.Error<MoodleWritePreview>(ex); }
        catch (ArgumentException ex) { return ToolResultHelper.Error<MoodleWritePreview>(ex.Message); }
        catch (InvalidOperationException ex) { return ToolResultHelper.Error<MoodleWritePreview>(ex.Message); }
    }

    [McpServerTool(Name = "moodle_confirm_write", Title = "Confirmar Escrita Moodle",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<MoodleWriteResult>))]
    [Description("Confirma e executa uma única vez uma escrita Moodle previamente preparada. Exige o mesmo usuário, conexão, escopo de escrita da família (ou moodle.write para funções sem classificação específica) e texto literal de confirmação.")]
    public async Task<CallToolResult> ConfirmWriteAsync(
        [Description("Identificador da ação pendente retornado por moodle_prepare_write.")] Guid pendingActionId,
        [Description("Texto de confirmação literal retornado na prévia.")] string confirmationText,
        [Description("Alias da mesma conexão Moodle usada na prévia.")] string? moodleAlias = null,
        CancellationToken cancellationToken = default)
    {
        connectionSelection.Alias = moodleAlias;
        try
        {
            var data = await writeService.ConfirmAsync(pendingActionId, confirmationText, cancellationToken);
            var isError = data.Status is "write_failed" or "execution_unknown";
            return Result(data, data.Status == "executed"
                ? $"Escrita '{data.Function}' executada uma única vez."
                : data.Status == "execution_unknown"
                    ? "A execução ficou desconhecida após a confirmação. Não repita a chamada; reconcilie a ação antes de qualquer nova prévia."
                    : "A escrita já havia sido confirmada anteriormente e não foi repetida.", isError);
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException ex) { return ToolResultHelper.Error<MoodleWriteResult>(ex); }
        catch (InvalidOperationException ex) { return ToolResultHelper.Error<MoodleWriteResult>(ex.Message); }
    }

    private static IReadOnlyDictionary<string, object?> ToParameters(JsonElement parameters) =>
        parameters.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.Ordinal);

    private static CallToolResult Success<T>(T data, string narration) => Result(data, narration, false);

    private static CallToolResult Result<T>(T data, string narration, bool isError)
    {
        var response = new ToolResponse<T>(isError ? "error" : "ok", data, [], AuditId: null, DateTimeOffset.UtcNow);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = narration }],
            StructuredContent = JsonSerializer.SerializeToElement(response),
            IsError = isError
        };
    }
}
