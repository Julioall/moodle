namespace Moodle.Domain;

public sealed class PendingMoodleAction
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string ToolName { get; init; } = string.Empty;

    public ToolRiskLevel RiskLevel { get; init; }

    public string CreatedBySubject { get; init; } = string.Empty;

    public string? CreatedByEmail { get; init; }

    public long? CreatedByMoodleUserId { get; init; }

    public long? CourseId { get; init; }

    public string PayloadJson { get; init; } = "{}";

    public string PreviewJson { get; init; } = "{}";

    public string ConfirmationText { get; init; } = string.Empty;

    public PendingActionStatus Status { get; private set; } = PendingActionStatus.PendingConfirmation;

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public string? ConfirmedBySubject { get; private set; }

    /// <summary>Worker that currently owns durable publication execution.</summary>
    public string? ExecutionOwner { get; private set; }

    public DateTimeOffset? ExecutionLeaseUntil { get; private set; }

    public int ExecutionAttemptCount { get; private set; }

    public DateTimeOffset? NextExecutionAttemptAt { get; private set; }

    public string? LastExecutionError { get; private set; }

    public string IdempotencyKey { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    public void MarkExpired()
    {
        if (Status == PendingActionStatus.PendingConfirmation)
        {
            Status = PendingActionStatus.Expired;
        }
    }

    public void Confirm(string confirmedBySubject, DateTimeOffset confirmedAt)
    {
        ConfirmedBySubject = confirmedBySubject;
        ConfirmedAt = confirmedAt;
        Status = PendingActionStatus.Confirmed;
    }

    public void Authorize(string confirmedBySubject, DateTimeOffset confirmedAt)
    {
        ConfirmedBySubject = confirmedBySubject;
        ConfirmedAt = confirmedAt;
        NextExecutionAttemptAt = null;
        Status = PendingActionStatus.Authorized;
    }

    public bool BeginExecution(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(workerId) || leaseDuration <= TimeSpan.Zero)
        {
            return false;
        }

        var leaseActiveForAnotherWorker = Status == PendingActionStatus.Executing &&
            ExecutionLeaseUntil is { } activeUntil &&
            activeUntil > now &&
            !string.Equals(ExecutionOwner, workerId.Trim(), StringComparison.Ordinal);
        if (leaseActiveForAnotherWorker ||
            (Status == PendingActionStatus.PartiallyCompleted &&
             NextExecutionAttemptAt is { } scheduledAt && scheduledAt > now) ||
            Status is not (PendingActionStatus.Confirmed or PendingActionStatus.Authorized or PendingActionStatus.PartiallyCompleted or PendingActionStatus.Executing))
        {
            return false;
        }

        if (!string.Equals(ExecutionOwner, workerId.Trim(), StringComparison.Ordinal))
        {
            ExecutionAttemptCount++;
        }

        ExecutionOwner = workerId.Trim();
        ExecutionLeaseUntil = now.Add(leaseDuration);
        NextExecutionAttemptAt = null;
        LastExecutionError = null;
        Status = PendingActionStatus.Executing;
        return true;
    }

    public void MarkExecuted()
    {
        Status = PendingActionStatus.Executed;
        ExecutionLeaseUntil = null;
        NextExecutionAttemptAt = null;
        LastExecutionError = null;
    }

    public void MarkFailed(string? error = null)
    {
        Status = PendingActionStatus.Failed;
        ExecutionLeaseUntil = null;
        NextExecutionAttemptAt = null;
        LastExecutionError = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
    }

    public void MarkPartiallyCompleted(string? error = null, DateTimeOffset? nextExecutionAttemptAt = null)
    {
        Status = PendingActionStatus.PartiallyCompleted;
        ExecutionLeaseUntil = null;
        NextExecutionAttemptAt = nextExecutionAttemptAt;
        LastExecutionError = string.IsNullOrWhiteSpace(error) ? null : error.Trim();
    }

    /// <summary>
    /// Cancela uma acao de publicacao que ainda nao terminou. Acoes executadas
    /// ou com resultado remoto desconhecido nunca podem ser apagadas por um
    /// cancelamento local; elas precisam permanecer disponiveis para auditoria
    /// e reconciliacao.
    /// </summary>
    public bool MarkCancelled(string? reason = null)
    {
        if (Status is PendingActionStatus.Executed or PendingActionStatus.ExecutionUnknown or PendingActionStatus.Cancelled)
        {
            return false;
        }

        Status = PendingActionStatus.Cancelled;
        ExecutionOwner = null;
        ExecutionLeaseUntil = null;
        NextExecutionAttemptAt = null;
        LastExecutionError = string.IsNullOrWhiteSpace(reason) ? LastExecutionError : reason.Trim();
        return true;
    }

    /// <summary>
    /// Compatibilidade para chamadores que não precisam do resultado da
    /// transição. O estado permanece protegido por MarkCancelled.
    /// </summary>
    public void Cancel(string? reason = null)
    {
        MarkCancelled(reason);
    }

    public void MarkExecutionUnknown(string? error = null)
    {
        // A confirmação é persistida com uma atualização atômica. O agregado
        // que iniciou o fluxo pode continuar com o snapshot
        // PendingConfirmation no mesmo DbContext; neste ponto, contudo, a
        // confirmação já foi validada antes de qualquer escrita remota.
        if (Status is PendingActionStatus.Confirmed or PendingActionStatus.PendingConfirmation or PendingActionStatus.Authorized or PendingActionStatus.Executing)
        {
            Status = PendingActionStatus.ExecutionUnknown;
            ExecutionLeaseUntil = null;
            NextExecutionAttemptAt = null;
            if (!string.IsNullOrWhiteSpace(error))
            {
                LastExecutionError = error.Trim();
            }
        }
    }

    public void ResolveExecutionUnknown(PendingActionStatus resolvedStatus)
    {
        if (Status != PendingActionStatus.ExecutionUnknown)
        {
            throw new InvalidOperationException($"A ação não está em execução desconhecida: {Status}.");
        }

        if (resolvedStatus is not (PendingActionStatus.Executed or PendingActionStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(resolvedStatus), "A reconciliação deve resolver para Executed ou Failed.");
        }

        Status = resolvedStatus;
        NextExecutionAttemptAt = null;
    }
}
