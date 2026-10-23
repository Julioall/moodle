using Moodle.Domain.Grading;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Estado durável de execução do lote. O channel em memória pode acelerar o
/// despacho, mas nunca substitui estas operações de claim/lease.
/// </summary>
public interface IGradingBatchJobStore
{
    Task<IReadOnlyList<GradingBatchLeaseClaim>> ClaimDueBatchesAsync(
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxBatches,
        CancellationToken cancellationToken);

    Task<GradingBatchLeaseClaim?> TryClaimBatchAsync(
        Guid batchId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> RenewBatchLeaseAsync(
        Guid batchId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> ReleaseBatchLeaseAsync(
        Guid batchId,
        string workerId,
        DateTimeOffset now,
        string? errorCode,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken);

    Task<bool> UpdateBatchCheckpointAsync(
        Guid batchId,
        string workerId,
        Guid itemId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<int> RecoverExpiredBatchLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reserva um item pendente para uma etapa de processamento interno.
    /// O claim é condicional e seguro para múltiplas réplicas.
    /// </summary>
    Task<GradingItemLeaseClaim?> TryClaimItemAsync(
        Guid batchId,
        Guid itemId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Claims a set of pending items atomically where the backing store can
    /// provide a bulk implementation. The default preserves compatibility
    /// with lightweight stores by delegating to the single-item operation.
    /// </summary>
    async Task<IReadOnlySet<Guid>> TryClaimItemsAsync(
        Guid batchId,
        IReadOnlyCollection<Guid> itemIds,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var claimed = new HashSet<Guid>();
        foreach (var itemId in itemIds.Distinct())
        {
            if (await TryClaimItemAsync(
                    batchId,
                    itemId,
                    workerId,
                    now,
                    leaseDuration,
                    cancellationToken) is not null)
            {
                claimed.Add(itemId);
            }
        }

        return claimed;
    }

    Task<bool> RenewItemLeaseAsync(
        Guid batchId,
        Guid itemId,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Renews all still-pending items in a processing window. This prevents
    /// queued items from expiring while an earlier item in the same window is
    /// taking a long time to process. Implementations may optimize this to a
    /// single conditional UPDATE; the default keeps lightweight stores
    /// compatible.
    /// </summary>
    async Task<int> RenewItemLeasesAsync(
        Guid batchId,
        IReadOnlyCollection<Guid> itemIds,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var renewed = 0;
        foreach (var itemId in itemIds.Distinct())
        {
            if (await RenewItemLeaseAsync(
                    batchId,
                    itemId,
                    workerId,
                    now,
                    leaseDuration,
                    cancellationToken))
            {
                renewed++;
            }
        }

        return renewed;
    }

    /// <summary>
    /// Releases item leases in one operation when supported. The default is
    /// intentionally conservative for compatibility stores.
    /// </summary>
    async Task<int> ReleaseItemLeasesAsync(
        Guid batchId,
        IReadOnlyCollection<Guid> itemIds,
        string workerId,
        DateTimeOffset now,
        string? errorCode,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken,
        string? errorPhase = null)
    {
        var released = 0;
        foreach (var itemId in itemIds.Distinct())
        {
            if (await ReleaseItemLeaseAsync(
                    batchId,
                    itemId,
                    workerId,
                    now,
                    errorCode,
                    nextAttemptAt,
                    cancellationToken,
                    errorPhase))
            {
                released++;
            }
        }

        return released;
    }

    Task<bool> ReleaseItemLeaseAsync(
        Guid batchId,
        Guid itemId,
        string workerId,
        DateTimeOffset now,
        string? errorCode,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken,
        string? errorPhase = null);

    Task<int> RecoverExpiredItemLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed record GradingBatchLeaseClaim(
    Guid BatchId,
    string WorkerId,
    DateTimeOffset LeaseUntil,
    int AttemptCount);

public sealed record GradingItemLeaseClaim(
    Guid BatchId,
    Guid ItemId,
    string WorkerId,
    DateTimeOffset LeaseUntil,
    int AttemptCount);
