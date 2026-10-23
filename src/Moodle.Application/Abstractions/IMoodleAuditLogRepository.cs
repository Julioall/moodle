using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public interface IMoodleAuditLogRepository
{
    Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken);

    Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(
        string correlationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<int> CountByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(
        Guid batchJobId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<int> CountByBatchJobIdAsync(
        Guid batchJobId,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
