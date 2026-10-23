using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Orquestrador de lotes de correção assistida com fila assíncrona.
/// EnqueueAsync publica na GradingBatchChannel e retorna imediatamente,
/// sem travar o chat. O processamento real é feito pelo GradingBatchWorkerService.
/// </summary>
public sealed class BackgroundGradingBatchOrchestrator(
    IGradingReviewRepository repository,
    IOptions<GradingLimitsOptions> limits,
    GradingBatchChannel channel,
    ILogger<BackgroundGradingBatchOrchestrator> logger,
    IGradingBatchJobStore? jobStore = null)
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
                "Lote {BatchId} em status {Status} nao aceita enfileiramento; enfileiramento ignorado.",
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

        var workItem = new GradingBatchWorkItem(batchId, DateTimeOffset.UtcNow);
        if (jobStore is null)
        {
            // Hosts sem persistência durável ainda dependem do canal para
            // recuperar o trabalho em memória.
            await channel.EnqueueAsync(workItem, cancellationToken);
        }
        else if (!channel.TryEnqueue(workItem))
        {
            // Não bloquear o request MCP quando houver uma fila grande. O
            // lote permanece Pending e será capturado pelo polling durável.
            logger.LogInformation(
                "Canal local cheio para o lote {BatchId}; o polling durável fará o despacho.",
                batchId);
        }

        logger.LogInformation(
            "Lote {BatchId} com {TotalItems} itens enfileirado para processamento em background.",
            batchId,
            totalItems);
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
