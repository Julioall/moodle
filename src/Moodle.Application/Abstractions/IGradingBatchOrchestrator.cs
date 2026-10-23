using Moodle.Domain.Grading;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Orquestra criação, enfileiramento e controle de ciclo de vida de lotes de correção assistida.
/// </summary>
public interface IGradingBatchOrchestrator
{
    /// <summary>
    /// Enfileira um lote existente para processamento.
    /// No MVP, processa inline de forma síncrona.
    /// </summary>
    Task EnqueueAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>
    /// Cancela um lote que ainda não foi completamente processado.
    /// </summary>
    Task CancelAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>
    /// Retorna um resumo do estado atual de processamento do lote.
    /// </summary>
    Task<GradingBatchOrchestratorStatus> GetStatusAsync(Guid batchId, CancellationToken cancellationToken);
}

public sealed record GradingBatchOrchestratorStatus(
    Guid BatchId,
    GradingBatchStatus BatchStatus,
    int TotalItems,
    int ProcessedItems,
    int ReadyItems,
    int BlockedItems,
    int FailedItems,
    bool IsQueued,
    string? LastError);
