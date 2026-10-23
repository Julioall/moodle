using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Processa itens individuais de correção assistida.
/// Extraído do antigo LocalGradingBatchOrchestrator para ser reutilizável
/// tanto inline quanto pelo worker assíncrono.
/// </summary>
public sealed class GradingItemProcessor(
    IGradingContextBuilder contextBuilder,
    IGradingAnalysisService analysisService,
    ILogger<GradingItemProcessor> logger,
    IGradingContextSnapshotStore? snapshotStore = null,
    IOptions<MoodleUniversalApiFeatureOptions>? resourceFeatures = null)
{
    public Task PrepareBatchAsync(
        AssistedGradingBatch batch,
        IReadOnlyCollection<AssistedGradingItem> items,
        CancellationToken cancellationToken)
    {
        return contextBuilder.PrepareBatchAsync(batch, items, cancellationToken);
    }

    public async Task ProcessItemAsync(
        AssistedGradingItem item,
        IGradingReviewRepository repository,
        CancellationToken cancellationToken,
        string? teacherInstructions = null,
        GradingContextOptions? contextOptions = null,
        Func<CancellationToken, Task>? checkpointAsync = null)
    {
        var effectiveContextOptions = contextOptions ?? new GradingContextOptions(
            IncludeRubric: true,
            IncludeSubmissionFiles: true,
            IncludeCourseMaterials: true,
            TeacherInstructions: teacherInstructions);
        item.MarkProcessingStage(GradingProcessingStage.Context);
        if (checkpointAsync is not null)
        {
            await checkpointAsync(cancellationToken);
        }
        var context = await contextBuilder.BuildAsync(
            item,
            effectiveContextOptions,
            cancellationToken);

        // Registra a identidade canônica junto ao item antes de qualquer resultado
        // de análise. Assim, inclusive contextos bloqueados deixam uma evidência
        // verificável do material que foi considerado pelo worker.
        var contextSnapshot = GradingContextSnapshotFactory.Create(
            item,
            context,
            effectiveContextOptions);
        if (item.ContextHash is not null &&
            !string.Equals(item.ContextHash, contextSnapshot.ContextHash, StringComparison.Ordinal))
        {
            contextSnapshot = GradingContextSnapshotFactory.Create(
                item,
                context,
                effectiveContextOptions,
                version: (item.ContextVersion ?? 0) + 1);
        }

        if (snapshotStore is not null)
        {
            // O documento é publicado antes da análise; se a persistência falhar,
            // o item não avança e o worker poderá retomá-lo com segurança.
            await snapshotStore.PublishAsync(contextSnapshot, cancellationToken);
        }
        item.RecordContextSnapshot(contextSnapshot);
        item.MarkProcessingStage(GradingProcessingStage.Analysis);
        if (checkpointAsync is not null)
        {
            await checkpointAsync(cancellationToken);
        }

        var readableText = FirstReadableText(context);
        if (string.IsNullOrWhiteSpace(readableText))
        {
            if (resourceFeatures?.Value.McpResourceSubmissionDeliveryEnabled == true)
            {
                // BuildAsync already loaded the artifacts. Reusing the
                // context avoids a second SELECT for every deferred file in a
                // large batch; the resource flag is part of GradingFileInfo.
                var hasDeferredSubmissionResource = context.AttachedFiles.Any(file =>
                    string.Equals(file.ArtifactType, "submission_file", StringComparison.OrdinalIgnoreCase) &&
                    file.OriginalResourceAvailable);
                // Legacy/test builders may not expose attached-file metadata.
                // Keep their behavior by consulting persistence only when the
                // context is genuinely empty; the production builder never
                // takes this branch after its batch preload.
                if (!hasDeferredSubmissionResource && context.AttachedFiles.Count == 0)
                {
                    hasDeferredSubmissionResource = (await repository.ListArtifactsByItemAsync(item.Id, cancellationToken))
                        .Any(artifact =>
                            string.Equals(artifact.ArtifactType, "submission_file", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(artifact.SourceUrl));
                }
                if (hasDeferredSubmissionResource)
                {
                    // A pré-validação local não interpreta a entrega quando o
                    // modo MCP está ativo. O modelo fará isso a partir do
                    // arquivo original, através do resource_link.
                    item.MarkAwaitingAiAnalysis(
                        "Entrega pendente de leitura via MCP Resource; o texto local não é utilizado.");
                    return;
                }
            }

            var blockerReason = context.Blockers.FirstOrDefault(b =>
                b.Contains("Submissão sem conteúdo legível", StringComparison.OrdinalIgnoreCase) ||
                b.Contains("Submissão não disponível", StringComparison.OrdinalIgnoreCase))
                ?? "Submissao sem conteudo legivel para correcao assistida.";
            logger.LogDebug(
                "Item {GradingItemId} bloqueado: {Reason}",
                item.Id,
                blockerReason);
            item.BlockAnalysis(blockerReason);
            return;
        }

        var result = await analysisService.AnalyzeAsync(
            new GradingAnalysisRequest(
                AssignmentName: $"Tarefa {context.AssignmentId}",
                MaxGrade: context.MaxGrade ?? 0m,
                ActivityDescription: context.AssignmentStatement,
                RubricOrCriteria: context.RubricDescription ?? context.Criteria,
                TeacherInstructions: context.TeacherInstructions,
                SubmissionText: readableText,
                FileHashes: context.AttachedFiles
                    .Select(file => file.Sha256)
                    .Where(hash => !string.IsNullOrWhiteSpace(hash))
                    .Select(hash => hash!)
                    .ToArray(),
                ContextHash: contextSnapshot.ContextHash),
            cancellationToken);

        // --- Fluxo IA-first: o serviço de análise nunca gera nota/feedback heurístico ---

        // Itens bloqueados (texto vazio, enunciado insuficiente)
        if (result.Blocks.Count > 0)
        {
            item.BlockAnalysis(string.Join(" ", result.Blocks));
            return;
        }

        // Itens prontos para IA (pré-validação diagnóstica aprovada)
        if (result.AnalysisStatus == AnalysisStatus.AwaitingAiAnalysis)
        {
            var diagnosticNotes = result.PrivateNotesToTeacher;

            // Appender observações de geração de critérios (separadas de TeacherInstructions)
            if (!string.IsNullOrWhiteSpace(context.CriteriaGenerationNotes))
            {
                diagnosticNotes = string.IsNullOrWhiteSpace(diagnosticNotes)
                    ? context.CriteriaGenerationNotes
                    : $"{diagnosticNotes} {context.CriteriaGenerationNotes}";
            }

            item.MarkAwaitingAiAnalysis(diagnosticNotes);
            return;
        }

        // Fallback: resultado inesperado — bloquear com motivo genérico
        item.BlockAnalysis(
            "Analise de correcao assistida nao gerou resultado processavel. Verifique os dados da atividade.");
    }

    public static void UpdateBatchCounters(
        AssistedGradingBatch batch,
        IReadOnlyList<AssistedGradingItem> items)
    {
        // ReadyItems representa somente rascunhos que aguardam revisão humana.
        // Itens ReadyToCommit são launchPending e não devem inflar essa métrica.
        var readyItems = items.Count(item => item.Status == GradingItemStatus.DraftReady);
        var blockedItems = items.Count(item => item.Status == GradingItemStatus.Blocked);
        var failedItems = items.Count(item => item.Status == GradingItemStatus.Failed);
        var processedItems = items.Count(item => item.Status is
            GradingItemStatus.DraftReady or
            GradingItemStatus.ReadyToCommit or
            GradingItemStatus.Committed or
            GradingItemStatus.Blocked or
            GradingItemStatus.Failed);

        batch.UpdateCounters(processedItems, readyItems, blockedItems, failedItems);

        if (items.Count > 0 && items.All(item => item.Status == GradingItemStatus.Committed))
        {
            batch.MarkCompleted();
        }
    }

    /// <summary>
    /// Carrega todos os itens de um lote via paginação.
    /// Reutilizado por múltiplos handlers que precisam iterar o lote inteiro.
    /// </summary>
    public static async Task<IReadOnlyList<AssistedGradingItem>> LoadAllBatchItemsAsync(
        IGradingReviewRepository repository,
        Guid batchId,
        CancellationToken cancellationToken,
        int pageSize = 100)
    {
        // O repositório aplica um limite de segurança de 400 itens por página,
        // alinhado ao limite de sublote. Isso mantém a memória limitada sem
        // transformar cada sublote de 400 em quatro consultas sequenciais.
        var effectivePageSize = Math.Clamp(pageSize, 1, 400);
        var allItems = new List<AssistedGradingItem>();
        var page = 1;
        while (true)
        {
            var pageItems = await repository.ListItemsByBatchAsync(batchId, page, effectivePageSize, cancellationToken);
            allItems.AddRange(pageItems);
            if (pageItems.Count < effectivePageSize)
            {
                break;
            }

            page++;
        }

        return allItems;
    }

    private static string? FirstReadableText(GradingContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.SubmissionText))
        {
            return context.SubmissionText;
        }

        return context.AttachedFiles
            .Select(file => file.ExtractedText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static string BuildPreliminaryTeacherNotes(GradingContext context)
    {
        return context.Blockers.Count == 0
            ? "Rascunho preliminar sem bloqueadores adicionais."
            : "Rascunho preliminar gerado com pendencias de contexto: " + string.Join(" ", context.Blockers);
    }
}
