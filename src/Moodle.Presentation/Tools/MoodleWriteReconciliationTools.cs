using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moodle.Application.MoodleApi;
using Moodle.Application.Tools;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Tools;

/// <summary>
/// Reconciliation remains available even when the universal write feature is
/// disabled, because specialized grading/message/forum writes can also end in
/// execution_unknown and must never be retried blindly.
/// </summary>
[McpServerToolType]
public sealed class MoodleWriteReconciliationTools(
    IMoodleWriteReconciliationService reconciliationService)
{
    [McpServerTool(Name = "moodle_reconcile_write", Title = "Reconciliar Escrita Moodle",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolResponse<MoodleWriteReconciliationResult>))]
    [MoodleToolMetadata(Family = "infrastructure", Classification = "R5", Kind = "controlled-write", CanonicalOperation = "moodle_reconcile_write", Structural = false,
        ExposureReason = "Reconcilia uma escrita ambígua sem reenviar a operação remota.")]
    [Description("Reconcilia uma escrita sem reenviá-la. Para correções de atividade, informe gradingItemId e use resolution='auto'; o Moodle será relido e somente um destino comprovado será marcado como aplicado. Para outras escritas, informe resolution='executed' ou 'not_applied' com evidência.")]
    public async Task<CallToolResult> ReconcileWriteAsync(
        [Description("Identificador da ação pendente que está em execution_unknown.")] Guid pendingActionId,
        [Description("Para correções, use auto. Para outras escritas: executed ou not_applied.")] string resolution = "auto",
        [Description("Obrigatório para corrigir um item de uma publicação de atividade.")] Guid? gradingItemId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await reconciliationService.ReconcileAsync(pendingActionId, resolution, gradingItemId, cancellationToken);
            var narration = data.Status switch
            {
                "reconciled" when data.GradingItemId is not null => "O item foi reconciliado sem nova chamada de escrita ao Moodle.",
                "reconciled" => "A ação foi reconciliada sem nova chamada de escrita ao Moodle.",
                "inconclusive" => "A leitura foi inconclusiva; o item continua incerto e não foi reenviado.",
                "conflict" => "O estado do Moodle diverge; o item continua incerto e não foi reenviado.",
                "already_reconciled" => "A ação ou o item já havia sido reconciliado.",
                _ => "A reconciliação terminou sem reenviar a escrita ao Moodle."
            };
            return Result(data, narration, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (MoodleApiException ex) { return ToolResultHelper.Error<MoodleWriteReconciliationResult>(ex); }
        catch (ArgumentException ex) { return ToolResultHelper.Error<MoodleWriteReconciliationResult>(ex.Message); }
        catch (InvalidOperationException ex) { return ToolResultHelper.Error<MoodleWriteReconciliationResult>(ex.Message); }
        catch (UnauthorizedAccessException ex) { return ToolResultHelper.Error<MoodleWriteReconciliationResult>(ex.Message); }
    }

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
