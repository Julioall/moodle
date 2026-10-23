using Microsoft.EntityFrameworkCore;

namespace Moodle.Infrastructure.Reports;

internal sealed class ReportJobLeaseRenewer(IDbContextFactory<ConnectorDbContext> dbContextFactory)
{
    public async Task<bool> RenewAsync(Guid reportJobId, Guid workerLeaseId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await dbContext.ReportJobs
            .Where(job => job.Id == reportJobId &&
                          job.Status == "running" &&
                          job.WorkerLeaseId == workerLeaseId && job.LeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.LeaseUntil, now.Add(ReportJobClaimer.LeaseDuration))
                .SetProperty(job => job.UpdatedAt, now), cancellationToken);
        return updated == 1;
    }
}
