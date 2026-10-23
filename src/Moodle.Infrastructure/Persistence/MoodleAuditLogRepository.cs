using Moodle.Application.Abstractions;
using Moodle.Domain;
using Microsoft.EntityFrameworkCore;

namespace Moodle.Infrastructure;

public sealed class MoodleAuditLogRepository(ConnectorDbContext dbContext) : IMoodleAuditLogRepository
{
    public async Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken)
    {
        await dbContext.MoodleAuditLogs.AddAsync(log, cancellationToken);
    }

    public async Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(
        string correlationId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return await dbContext.MoodleAuditLogs
            .AsNoTracking()
            .Where(log => log.CorrelationId == correlationId)
            .OrderBy(log => log.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
    }

    public Task<int> CountByCorrelationIdAsync(
        string correlationId,
        CancellationToken cancellationToken)
    {
        return dbContext.MoodleAuditLogs
            .AsNoTracking()
            .CountAsync(log => log.CorrelationId == correlationId, cancellationToken);
    }

    public async Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(
        Guid batchJobId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return await dbContext.MoodleAuditLogs
            .AsNoTracking()
            .Where(log => log.BatchJobId == batchJobId)
            .OrderBy(log => log.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
    }

    public Task<int> CountByBatchJobIdAsync(
        Guid batchJobId,
        CancellationToken cancellationToken)
    {
        return dbContext.MoodleAuditLogs
            .AsNoTracking()
            .CountAsync(log => log.BatchJobId == batchJobId, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
