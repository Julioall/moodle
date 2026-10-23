using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public sealed record PendingActionConfirmationClaimResult(
    bool ConfirmedByCaller,
    PendingActionStatus Status,
    DateTimeOffset? ConfirmedAt);

public sealed record PendingActionReconciliationClaimResult(
    bool ResolvedByCaller,
    PendingActionStatus Status,
    string? AuditId);

public sealed record PendingActionExecutionClaimResult(
    bool Claimed,
    PendingActionStatus Status,
    DateTimeOffset? LeaseUntil,
    int AttemptCount,
    long FencingToken = 0);

public interface IPendingMoodleActionRepository
{
    Task AddAsync(PendingMoodleAction action, CancellationToken cancellationToken);

    Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a still-unconfirmed grading preview and releases its provisional
    /// target claims in one transaction. A competing authorization wins or
    /// loses atomically with this operation.
    /// </summary>
    Task<bool> TryCancelPendingGradingPublicationAsync(
        Guid id,
        Guid? publicationId,
        string reason,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>
    /// Lista ações de publicação que apontam para um lote/execução. O
    /// cancelamento de um lote usa isso para não deixar uma confirmação antiga
    /// publicar depois que o operador decidiu abandonar aquele fluxo.
    /// </summary>
    Task<IReadOnlyList<PendingMoodleAction>> ListGradingPublicationsByBatchIdAsync(
        Guid batchJobId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingMoodleAction>>([]);

    /// <summary>
    /// Lists grading publications for a group of batches in one repository
    /// operation. The default preserves compatibility with lightweight
    /// repositories; durable stores should override it to avoid one query per
    /// sub-batch when a grading run is large.
    /// </summary>
    async Task<IReadOnlyList<PendingMoodleAction>> ListGradingPublicationsByBatchIdsAsync(
        IReadOnlyCollection<Guid> batchJobIds,
        CancellationToken cancellationToken)
    {
        var ids = batchJobIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var results = await Task.WhenAll(ids.Select(id =>
            ListGradingPublicationsByBatchIdAsync(id, cancellationToken)));
        return results.SelectMany(result => result).DistinctBy(action => action.Id).ToArray();
    }

    Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(
        Guid id,
        string confirmedBySubject,
        DateTimeOffset confirmedAt,
        MoodleAuditLog confirmationAudit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Authorizes a grading publication without declaring its remote writes
    /// complete. The default keeps test/legacy repositories compatible; the
    /// durable implementation atomically transitions to Authorized.
    /// </summary>
    Task<PendingActionConfirmationClaimResult> TryAuthorizeWithAuditAsync(
        Guid id,
        string confirmedBySubject,
        DateTimeOffset confirmedAt,
        MoodleAuditLog confirmationAudit,
        CancellationToken cancellationToken) =>
        TryConfirmWithAuditAsync(id, confirmedBySubject, confirmedAt, confirmationAudit, cancellationToken);

    /// <summary>Claims an authorized, partial, or expired executing action for a worker.</summary>
    Task<PendingActionExecutionClaimResult> TryBeginExecutionAsync(
        Guid id,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("O repositorio nao oferece execucao duravel de acoes.");

    /// <summary>
    /// Lista publicações autorizadas ou com lease de execução expirado para retomada pelo
    /// worker. A implementação durável deve filtrar somente os dois tools de
    /// lançamento de notas; nunca executar ações genéricas automaticamente.
    /// </summary>
    Task<IReadOnlyList<PendingMoodleAction>> ListRecoverableGradingPublicationsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingMoodleAction>>([]);

    /// <summary>
    /// Returns publication claim identifiers from terminal actions. A
    /// terminal action can still have active target claims if the process
    /// crashed between the two persistence calls; the publication worker
    /// releases those claims idempotently on its next sweep.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListTerminalGradingPublicationIdsAsync(
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    /// <summary>Extends an active execution lease for a long-running publication.</summary>
    Task<bool> TryRenewExecutionLeaseAsync(
        Guid id,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <summary>
    /// Renews only the lease generation that acquired the execution claim.
    /// The compatibility implementation delegates to the owner-only contract;
    /// durable repositories must also compare the fencing token.
    /// </summary>
    Task<bool> TryRenewExecutionLeaseAsync(
        Guid id,
        string workerId,
        long fencingToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        TryRenewExecutionLeaseAsync(id, workerId, now, leaseDuration, cancellationToken);

    /// <summary>
    /// Atomically resolves an execution-unknown action and records the
    /// operator decision. Implementations backed by a database must claim the
    /// row with a conditional update so two operators cannot resolve it twice.
    /// </summary>
    Task<PendingActionReconciliationClaimResult> TryResolveExecutionUnknownWithAuditAsync(
        Guid id,
        PendingActionStatus resolvedStatus,
        MoodleAuditLog reconciliationAudit,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("O repositorio não oferece reconciliação atômica.");

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
