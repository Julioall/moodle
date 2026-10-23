using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Implementação de orquestração inline (stub MVP) sem fila de mensagens real.
/// Marca itens imediatamente sem processamento assíncrono.
/// Quando uma fila real for integrada, substituir por implementação baseada em workers.
/// </summary>
public sealed class LocalGradingBatchOrchestrator(
    IGradingReviewRepository repository,
    IOptions<GradingLimitsOptions> limits,
    GradingItemProcessor itemProcessor,
    ILogger<LocalGradingBatchOrchestrator> logger)
    : IGradingBatchOrchestrator
{
    public async Task EnqueueAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(batchId));
        }

        var batch = await repository.GetBatchAsync(batchId, cancellationToken)
            ?? throw new InvalidOperationException("Lote nao encontrado para enfileirar.");

        if (batch.Status is not (GradingBatchStatus.Pending or GradingBatchStatus.Processing))
        {
            logger.LogDebug(
                "Lote {BatchId} em status {Status} nao aceita enfileiramento local; enfileiramento ignorado.",
                batchId,
                batch.Status);
            return;
        }

        var maxItems = limits.Value.MaxBatchItems;
        var totalItems = await repository.CountItemsByBatchAsync(batchId, cancellationToken);

        if (totalItems > maxItems)
        {
            throw new InvalidOperationException(
                $"O lote contém {totalItems} itens mas o limite configurado é {maxItems}.");
        }

        logger.LogInformation(
            "Lote {BatchId} com {TotalItems} itens enfileirado para processamento inline (MVP).",
            batchId,
            totalItems);

        var items = await GradingItemProcessor.LoadAllBatchItemsAsync(
            repository,
            batchId,
            cancellationToken,
            maxItems);

        foreach (var item in items.Where(item => item.Status == GradingItemStatus.Pending))
        {
            try
            {
                await itemProcessor.ProcessItemAsync(
                    item,
                    repository,
                    cancellationToken,
                    batch.TeacherInstructions,
                    new GradingContextOptions(
                        batch.IncludeRubric,
                        batch.IncludeSubmissionFiles,
                        batch.IncludeCourseMaterials,
                        batch.TeacherInstructions));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var errorDescriptor = MoodleErrorContract.Describe(ex);
                logger.LogError(
                    "Falha recuperável ao processar item {GradingItemId} do lote {BatchId}, fase {ErrorPhase}, código {ErrorCode}.",
                    item.Id,
                    batchId,
                    GradingFailurePhase.FromProcessingStage(item.ProcessingStage),
                    errorDescriptor.ErrorCode);
                item.MarkAnalysisFailed(
                    $"Falha ao processar este item de correcao assistida: {errorDescriptor.Message}",
                    errorDescriptor.ErrorCode,
                    GradingFailurePhase.FromProcessingStage(item.ProcessingStage));
            }
        }

        GradingItemProcessor.UpdateBatchCounters(batch, items);
        await repository.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(batchId));
        }

        var batch = await repository.GetBatchAsync(batchId, cancellationToken)
            ?? throw new InvalidOperationException("Lote nao encontrado para cancelar.");

        if (batch.Status is GradingBatchStatus.Completed or GradingBatchStatus.Cancelled)
        {
            logger.LogDebug(
                "Lote {BatchId} em status {Status} nao pode ser cancelado.",
                batchId,
                batch.Status);
            return;
        }

        batch.Cancel();
        await repository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Lote {BatchId} cancelado.", batchId);
    }

    public async Task<GradingBatchOrchestratorStatus> GetStatusAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("O lote e obrigatorio.", nameof(batchId));
        }

        var batch = await repository.GetBatchAsync(batchId, cancellationToken)
            ?? throw new InvalidOperationException("Lote nao encontrado.");

        return new GradingBatchOrchestratorStatus(
            batch.Id,
            batch.Status,
            batch.TotalItems,
            batch.ProcessedItems,
            batch.ReadyItems,
            batch.BlockedItems,
            batch.FailedItems,
            IsQueued: batch.Status is GradingBatchStatus.Pending or GradingBatchStatus.Processing,
            LastError: null);
    }
}
