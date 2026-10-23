using Microsoft.EntityFrameworkCore;
using Moodle.Application.Reports.Templates;

namespace Moodle.Infrastructure.Reports;

internal sealed class ReportJobPublisher(ConnectorDbContext db)
{
    public async Task<bool> PublishAsync(ReportJobEntity job, Guid leaseId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational())
        {
            if (job.Status != "running" || job.WorkerLeaseId != leaseId || job.LeaseUntil <= now) return false;
            Complete(job, now);
            await db.SaveChangesAsync(ct);
            return true;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var owners = await db.UserAccounts.FromSqlInterpolated($"SELECT * FROM user_accounts WHERE \"Id\" = {job.OwnerId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        if (owners.Count != 1) return false;
        if (await ReportStorageCalculator.GetUsedBytesAsync(db, job.OwnerId, ct, job.Id) + job.FileSizeBytes > ReportStorageCalculator.LimitBytes)
            throw new ReportDatasetException("report_storage_limit_exceeded");
        now = DateTimeOffset.UtcNow;
        var updated = await db.ReportJobs.Where(j => j.Id == job.Id && j.OwnerId == job.OwnerId &&
                j.Status == "running" && j.WorkerLeaseId == leaseId && j.LeaseUntil > now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "completed")
                .SetProperty(j => j.WorkerLeaseId, (Guid?)null).SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.ProgressPercent, 100).SetProperty(j => j.CompletedAt, now).SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.ProcessedCourses, job.ProcessedCourses).SetProperty(j => j.TotalCourses, job.TotalCourses)
                .SetProperty(j => j.CourseNamesJson, job.CourseNamesJson).SetProperty(j => j.ResultMetadataJson, job.ResultMetadataJson)
                .SetProperty(j => j.FileName, job.FileName).SetProperty(j => j.ContentType, job.ContentType)
                .SetProperty(j => j.FileSizeBytes, job.FileSizeBytes).SetProperty(j => j.ArtifactBlobKey, job.ArtifactBlobKey)
                .SetProperty(j => j.ContentText, (string?)null).SetProperty(j => j.ContentBase64, (string?)null), ct);
        await transaction.CommitAsync(ct);
        db.Entry(job).State = EntityState.Detached;
        if (updated != 1) return false;
        Complete(job, now);
        return true;
    }

    public async Task ProgressAsync(ReportJobEntity job, Guid leaseId, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) { await db.SaveChangesAsync(ct); return; }
        var now = DateTimeOffset.UtcNow;
        var updated = await db.ReportJobs.Where(j => j.Id == job.Id && j.OwnerId == job.OwnerId &&
                j.Status == "running" && j.WorkerLeaseId == leaseId && j.LeaseUntil > now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ProgressPercent, job.ProgressPercent)
                .SetProperty(j => j.ProcessedCourses, job.ProcessedCourses).SetProperty(j => j.TotalCourses, job.TotalCourses)
                .SetProperty(j => j.CourseNamesJson, job.CourseNamesJson).SetProperty(j => j.UpdatedAt, now), ct);
        db.Entry(job).State = EntityState.Detached;
        if (updated != 1) throw new ReportDatasetException("report_lease_lost");
    }

    public async Task FailAsync(ReportJobEntity job, Guid leaseId, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational())
        {
            job.Status = "failed"; job.ErrorMessage = message; job.CompletedAt = now; job.UpdatedAt = now;
            job.WorkerLeaseId = null; job.LeaseUntil = null;
            await db.SaveChangesAsync();
            return;
        }
        await db.ReportJobs.Where(j => j.Id == job.Id && j.OwnerId == job.OwnerId &&
                j.Status == "running" && j.WorkerLeaseId == leaseId && j.LeaseUntil > now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "failed").SetProperty(j => j.ErrorMessage, message)
                .SetProperty(j => j.CompletedAt, now).SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.WorkerLeaseId, (Guid?)null).SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null));
        db.Entry(job).State = EntityState.Detached;
    }

    private static void Complete(ReportJobEntity job, DateTimeOffset now)
    {
        job.Status = "completed"; job.WorkerLeaseId = null; job.LeaseUntil = null;
        job.ProgressPercent = 100; job.CompletedAt = now; job.UpdatedAt = now;
    }
}
