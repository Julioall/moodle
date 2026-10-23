using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Infrastructure;

public sealed class PendingMoodleActionRepository(ConnectorDbContext dbContext) : IPendingMoodleActionRepository
{
    private bool IsInMemory => dbContext.Database.ProviderName?.Contains(
        "InMemory",
        StringComparison.OrdinalIgnoreCase) == true;

    public async Task AddAsync(PendingMoodleAction action, CancellationToken cancellationToken)
    {
        await dbContext.PendingMoodleActions.AddAsync(action, cancellationToken);
    }

    public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.PendingMoodleActions.SingleOrDefaultAsync(action => action.Id == id, cancellationToken);
    }

    public async Task<bool> TryCancelPendingGradingPublicationAsync(
        Guid id,
        Guid? publicationId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        if (IsInMemory)
        {
            var action = await dbContext.PendingMoodleActions
                .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (action is null ||
                action.ToolName is not ("criar_previa_lancamento_lote" or "confirmar_lancamento_lote_moodle") ||
                action.Status != PendingActionStatus.PendingConfirmation)
            {
                return false;
            }

            action.Cancel(reason.Trim());
            if (publicationId is Guid inMemoryPublicationId && inMemoryPublicationId != Guid.Empty)
            {
                var claims = await dbContext.GradingPublicationClaims
                    .Where(claim => claim.PublicationId == inMemoryPublicationId && claim.Status == "AwaitingConfirmation")
                    .ToArrayAsync(cancellationToken);
                foreach (var claim in claims)
                {
                    claim.Status = "Released";
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var cancelled = await dbContext.PendingMoodleActions
            .Where(action => action.Id == id &&
                             (action.ToolName == "criar_previa_lancamento_lote" ||
                              action.ToolName == "confirmar_lancamento_lote_moodle") &&
                             action.Status == PendingActionStatus.PendingConfirmation)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(action => action.Status, PendingActionStatus.Cancelled)
                .SetProperty(action => action.LastExecutionError, reason.Trim()), cancellationToken);
        if (cancelled != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (publicationId is Guid targetPublicationId && targetPublicationId != Guid.Empty)
        {
            await dbContext.GradingPublicationClaims
                .Where(claim => claim.PublicationId == targetPublicationId &&
                                claim.Status == "AwaitingConfirmation")
                .ExecuteUpdateAsync(setters => setters.SetProperty(claim => claim.Status, "Released"), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PendingMoodleAction>> ListGradingPublicationsByBatchIdAsync(
        Guid batchJobId,
        CancellationToken cancellationToken)
    {
        return await ListGradingPublicationsByBatchIdsAsync([batchJobId], cancellationToken);
    }

    public async Task<IReadOnlyList<PendingMoodleAction>> ListGradingPublicationsByBatchIdsAsync(
        IReadOnlyCollection<Guid> batchJobIds,
        CancellationToken cancellationToken)
    {
        var targetBatchIds = batchJobIds.Where(id => id != Guid.Empty).ToHashSet();
        if (targetBatchIds.Count == 0)
        {
            return [];
        }

        // PayloadJson is stored as PostgreSQL jsonb. Do not translate a
        // string.Contains predicate against that column: depending on the
        // Npgsql/provider version it can produce an invalid text operator and
        // make local batch cancellation fail before the batch is cancelled.
        // The result set is restricted to the two grading-publication tools
        // and the exact batch id is verified below in memory.
        var candidates = await dbContext.PendingMoodleActions
            .Where(action =>
                (action.ToolName == "criar_previa_lancamento_lote" ||
                 action.ToolName == "confirmar_lancamento_lote_moodle") &&
                action.Status != PendingActionStatus.Executed &&
                action.Status != PendingActionStatus.Cancelled)
            .ToArrayAsync(cancellationToken);

        return candidates
            .Where(action => PayloadTargetsAnyBatch(action.PayloadJson, targetBatchIds))
            .ToArray();
    }

    public async Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(
        Guid id,
        string confirmedBySubject,
        DateTimeOffset confirmedAt,
        MoodleAuditLog confirmationAudit,
        CancellationToken cancellationToken)
    {
        return await TryConfirmCoreAsync(
            id,
            confirmedBySubject,
            confirmedAt,
            confirmationAudit,
            PendingActionStatus.Confirmed,
            cancellationToken);
    }

    public async Task<PendingActionConfirmationClaimResult> TryAuthorizeWithAuditAsync(
        Guid id,
        string confirmedBySubject,
        DateTimeOffset confirmedAt,
        MoodleAuditLog confirmationAudit,
        CancellationToken cancellationToken)
    {
        return await TryConfirmCoreAsync(
            id,
            confirmedBySubject,
            confirmedAt,
            confirmationAudit,
            PendingActionStatus.Authorized,
            cancellationToken);
    }

    private async Task<PendingActionConfirmationClaimResult> TryConfirmCoreAsync(
        Guid id,
        string confirmedBySubject,
        DateTimeOffset confirmedAt,
        MoodleAuditLog confirmationAudit,
        PendingActionStatus targetStatus,
        CancellationToken cancellationToken)
    {
        if (IsInMemory)
        {
            var inMemoryAction = await dbContext.PendingMoodleActions
                .SingleOrDefaultAsync(action => action.Id == id, cancellationToken);
            if (inMemoryAction is null)
            {
                throw new InvalidOperationException("Acao pendente nao encontrada.");
            }

            var canTransition =
                (inMemoryAction.Status == PendingActionStatus.PendingConfirmation &&
                 inMemoryAction.ExpiresAt > confirmedAt) ||
                (targetStatus == PendingActionStatus.Authorized &&
                 inMemoryAction.Status == PendingActionStatus.PartiallyCompleted);
            if (canTransition)
            {
                if (targetStatus == PendingActionStatus.Authorized)
                {
                    inMemoryAction.Authorize(confirmedBySubject, confirmedAt);
                }
                else
                {
                    inMemoryAction.Confirm(confirmedBySubject, confirmedAt);
                }

                await dbContext.MoodleAuditLogs.AddAsync(confirmationAudit, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new PendingActionConfirmationClaimResult(true, targetStatus, confirmedAt);
            }

            if (inMemoryAction.Status == PendingActionStatus.PendingConfirmation &&
                inMemoryAction.ExpiresAt <= confirmedAt)
            {
                inMemoryAction.MarkExpired();
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return new PendingActionConfirmationClaimResult(
                false,
                inMemoryAction.Status,
                inMemoryAction.ConfirmedAt);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.PendingMoodleActions
            .Where(action => action.Id == id &&
                             ((action.Status == PendingActionStatus.PendingConfirmation &&
                               action.ExpiresAt > confirmedAt) ||
                              // A partial durable publication is terminal for
                              // its previous attempt, but an explicit repeat
                              // confirmation is allowed to requeue the
                              // unresolved items after the original preview
                              // TTL has elapsed.
                              (targetStatus == PendingActionStatus.Authorized &&
                               action.Status == PendingActionStatus.PartiallyCompleted)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(action => action.Status, targetStatus)
                .SetProperty(action => action.ConfirmedBySubject, confirmedBySubject)
                .SetProperty(action => action.ConfirmedAt, confirmedAt), cancellationToken);

        if (updated == 1)
        {
            await dbContext.MoodleAuditLogs.AddAsync(confirmationAudit, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await RefreshTrackedActionAsync(id, cancellationToken);
            return new PendingActionConfirmationClaimResult(true, targetStatus, confirmedAt);
        }

        await dbContext.PendingMoodleActions
            .Where(action => action.Id == id &&
                             action.Status == PendingActionStatus.PendingConfirmation &&
                             action.ExpiresAt <= confirmedAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(action => action.Status, PendingActionStatus.Expired), cancellationToken);

        var current = await dbContext.PendingMoodleActions
            .AsNoTracking()
            .SingleAsync(action => action.Id == id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PendingActionConfirmationClaimResult(false, current.Status, current.ConfirmedAt);
    }

    public async Task<PendingActionExecutionClaimResult> TryBeginExecutionAsync(
        Guid id,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(workerId) || leaseDuration <= TimeSpan.Zero)
        {
            return new PendingActionExecutionClaimResult(false, PendingActionStatus.Failed, null, 0);
        }

        var normalizedWorkerId = workerId.Trim();
        var leaseUntil = now.Add(leaseDuration);
        var current = await dbContext.PendingMoodleActions
            .AsNoTracking()
            .SingleOrDefaultAsync(action => action.Id == id, cancellationToken);
        if (current is null)
        {
            return new PendingActionExecutionClaimResult(false, PendingActionStatus.Failed, null, 0);
        }

        if (current.Status is PendingActionStatus.Executed or PendingActionStatus.Failed or PendingActionStatus.ExecutionUnknown)
        {
            return new PendingActionExecutionClaimResult(false, current.Status, current.ExecutionLeaseUntil, current.ExecutionAttemptCount, current.ExecutionAttemptCount);
        }

        if (IsInMemory)
        {
            var tracked = await dbContext.PendingMoodleActions
                .SingleAsync(action => action.Id == id, cancellationToken);
            if (!tracked.BeginExecution(normalizedWorkerId, now, leaseDuration))
            {
                return new PendingActionExecutionClaimResult(
                false,
                tracked.Status,
                tracked.ExecutionLeaseUntil,
                tracked.ExecutionAttemptCount,
                tracked.ExecutionAttemptCount);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return new PendingActionExecutionClaimResult(
                true,
                tracked.Status,
                tracked.ExecutionLeaseUntil,
                tracked.ExecutionAttemptCount,
                tracked.ExecutionAttemptCount);
        }

        var updated = await dbContext.PendingMoodleActions
            .Where(action => action.Id == id &&
                (action.Status == PendingActionStatus.Confirmed ||
                 action.Status == PendingActionStatus.Authorized ||
                 (action.Status == PendingActionStatus.PartiallyCompleted &&
                  action.NextExecutionAttemptAt != null && action.NextExecutionAttemptAt <= now) ||
                 (action.Status == PendingActionStatus.Executing &&
                  (action.ExecutionLeaseUntil == null || action.ExecutionLeaseUntil <= now || action.ExecutionOwner == normalizedWorkerId))))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(action => action.Status, PendingActionStatus.Executing)
                .SetProperty(action => action.ExecutionOwner, normalizedWorkerId)
                .SetProperty(action => action.ExecutionLeaseUntil, leaseUntil)
                .SetProperty(action => action.NextExecutionAttemptAt, (DateTimeOffset?)null)
                .SetProperty(action => action.ExecutionAttemptCount, action => action.ExecutionAttemptCount + 1)
                .SetProperty(action => action.LastExecutionError, (string?)null), cancellationToken);

        if (updated != 1)
        {
            var observed = await dbContext.PendingMoodleActions
                .AsNoTracking()
                .SingleAsync(action => action.Id == id, cancellationToken);
            return new PendingActionExecutionClaimResult(false, observed.Status, observed.ExecutionLeaseUntil, observed.ExecutionAttemptCount, observed.ExecutionAttemptCount);
        }

        await RefreshTrackedActionAsync(id, cancellationToken);
        return new PendingActionExecutionClaimResult(
            true,
            PendingActionStatus.Executing,
            leaseUntil,
            current.ExecutionAttemptCount + 1,
            current.ExecutionAttemptCount + 1);
    }

    public async Task<IReadOnlyList<PendingMoodleAction>> ListRecoverableGradingPublicationsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 100);
        return await dbContext.PendingMoodleActions
            .AsNoTracking()
            .Where(action =>
                (action.ToolName == "criar_previa_lancamento_lote" ||
                 action.ToolName == "confirmar_lancamento_lote_moodle") &&
                // Confirmed is retained for actions created before the
                // Authorized state was introduced. They must still be
                // recoverable after a process restart; new public calls use
                // Authorized and never rely on this compatibility branch.
                (action.Status == PendingActionStatus.Confirmed ||
                 action.Status == PendingActionStatus.Authorized ||
                 (action.Status == PendingActionStatus.PartiallyCompleted &&
                  action.NextExecutionAttemptAt != null && action.NextExecutionAttemptAt <= now) ||
                 (action.Status == PendingActionStatus.Executing &&
                  (action.ExecutionLeaseUntil == null || action.ExecutionLeaseUntil <= now))))
            // Current authorizations must not wait behind legacy Confirmed
            // actions that can be permanently blocked by missing historical
            // connection context. Prioritize the durable states that can
            // actually advance, while preserving FIFO within each state.
            .OrderBy(action => action.Status == PendingActionStatus.Authorized ? 0 :
                               action.Status == PendingActionStatus.PartiallyCompleted ? 1 : 2)
            .ThenBy(action => action.CreatedAt)
            .ThenBy(action => action.Id)
            .Take(safeLimit)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> ListTerminalGradingPublicationIdsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var payloads = await dbContext.PendingMoodleActions
            .AsNoTracking()
            .Where(action =>
                (action.ToolName == "criar_previa_lancamento_lote" ||
                 action.ToolName == "confirmar_lancamento_lote_moodle") &&
                (action.Status == PendingActionStatus.Executed ||
                 action.Status == PendingActionStatus.Failed ||
                 (action.Status == PendingActionStatus.PartiallyCompleted &&
                  action.NextExecutionAttemptAt == null)))
            .OrderByDescending(action => action.CreatedAt)
            .Take(safeLimit)
            .Select(action => action.PayloadJson)
            .ToArrayAsync(cancellationToken);

        var publicationIds = new HashSet<Guid>();
        foreach (var payloadJson in payloads)
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                if ((!root.TryGetProperty("publicationId", out var publicationIdElement) &&
                     !root.TryGetProperty("PublicationId", out publicationIdElement)) ||
                    publicationIdElement.ValueKind != JsonValueKind.String ||
                    !publicationIdElement.TryGetGuid(out var publicationId) ||
                    publicationId == Guid.Empty)
                {
                    continue;
                }

                publicationIds.Add(publicationId);
            }
            catch (JsonException)
            {
                // An invalid historical payload cannot be used to resolve a
                // claim; leave it for manual audit instead of failing the
                // whole publication recovery sweep.
            }
        }

        return publicationIds.ToArray();
    }

    public async Task<bool> TryRenewExecutionLeaseAsync(
        Guid id,
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(workerId) || leaseDuration <= TimeSpan.Zero)
        {
            return false;
        }

        var normalizedWorkerId = workerId.Trim();
        var updated = await dbContext.PendingMoodleActions
            .Where(action => action.Id == id &&
                             action.Status == PendingActionStatus.Executing &&
                             action.ExecutionOwner == normalizedWorkerId &&
                             action.ExecutionLeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                action => action.ExecutionLeaseUntil,
                now.Add(leaseDuration)), cancellationToken);
        return updated == 1;
    }

    public async Task<bool> TryRenewExecutionLeaseAsync(
        Guid id,
        string workerId,
        long fencingToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(workerId) || fencingToken <= 0 || leaseDuration <= TimeSpan.Zero)
        {
            return false;
        }

        var normalizedWorkerId = workerId.Trim();
        var updated = await dbContext.PendingMoodleActions
            .Where(action => action.Id == id &&
                             action.Status == PendingActionStatus.Executing &&
                             action.ExecutionOwner == normalizedWorkerId &&
                             action.ExecutionAttemptCount == fencingToken &&
                             action.ExecutionLeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                action => action.ExecutionLeaseUntil,
                now.Add(leaseDuration)), cancellationToken);
        return updated == 1;
    }

    public async Task<PendingActionReconciliationClaimResult> TryResolveExecutionUnknownWithAuditAsync(
        Guid id,
        PendingActionStatus resolvedStatus,
        MoodleAuditLog reconciliationAudit,
        CancellationToken cancellationToken)
    {
        if (resolvedStatus is not (PendingActionStatus.Executed or PendingActionStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(resolvedStatus));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var updated = await dbContext.PendingMoodleActions
            .Where(action => action.Id == id && action.Status == PendingActionStatus.ExecutionUnknown)
            .ExecuteUpdateAsync(setters => setters.SetProperty(action => action.Status, resolvedStatus), cancellationToken);

        if (updated == 1)
        {
            await dbContext.MoodleAuditLogs.AddAsync(reconciliationAudit, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PendingActionReconciliationClaimResult(true, resolvedStatus, reconciliationAudit.Id.ToString("N"));
        }

        var current = await dbContext.PendingMoodleActions
            .AsNoTracking()
            .SingleAsync(action => action.Id == id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PendingActionReconciliationClaimResult(false, current.Status, null);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshTrackedActionAsync(Guid id, CancellationToken cancellationToken)
    {
        var tracked = dbContext.PendingMoodleActions.Local.SingleOrDefault(action => action.Id == id);
        if (tracked is not null)
        {
            await dbContext.Entry(tracked).ReloadAsync(cancellationToken);
        }
    }

    private static bool PayloadTargetsAnyBatch(string payloadJson, IReadOnlySet<Guid> batchJobIds)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("batchJobId", out var value) &&
                !root.TryGetProperty("BatchJobId", out value))
            {
                return false;
            }

            return value.ValueKind == JsonValueKind.String &&
                   value.TryGetGuid(out var parsed) &&
                   batchJobIds.Contains(parsed);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
