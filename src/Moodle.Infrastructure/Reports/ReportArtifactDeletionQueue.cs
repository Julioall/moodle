using Microsoft.EntityFrameworkCore;

namespace Moodle.Infrastructure.Reports;

internal sealed class ReportArtifactDeletionQueue(IDbContextFactory<ConnectorDbContext> dbContextFactory)
{
    public async Task EnqueueAsync(string blobKey, Exception exception)
    {
        var now = DateTimeOffset.UtcNow;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        dbContext.ReportArtifactDeletions.Add(new ReportArtifactDeletionEntity
        {
            Id = Guid.NewGuid(),
            ArtifactBlobKey = blobKey,
            RequestedAt = now,
            AttemptCount = 1,
            NextAttemptAt = now.AddSeconds(5),
            LastError = $"Artifact deletion failed ({exception.GetType().Name}).",
        });
        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    public async Task PersistFailedJobArtifactAsync(ReportJobEntity job)
    {
        if (string.IsNullOrWhiteSpace(job.ArtifactBlobKey))
        {
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        if (!dbContext.Database.IsRelational())
        {
            var candidate = await dbContext.ReportJobs.SingleOrDefaultAsync(candidate =>
                candidate.Id == job.Id &&
                candidate.Status == "failed" &&
                candidate.ArtifactBlobKey == null);
            if (candidate is null)
            {
                return;
            }

            candidate.ArtifactBlobKey = job.ArtifactBlobKey;
            candidate.FileSizeBytes = job.FileSizeBytes;
            candidate.FileName = job.FileName;
            candidate.ContentType = job.ContentType;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return;
        }

        await dbContext.ReportJobs
            .Where(candidate => candidate.Id == job.Id &&
                                candidate.Status == "failed" &&
                                candidate.ArtifactBlobKey == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.ArtifactBlobKey, job.ArtifactBlobKey)
                .SetProperty(candidate => candidate.FileSizeBytes, job.FileSizeBytes)
                .SetProperty(candidate => candidate.FileName, job.FileName)
                .SetProperty(candidate => candidate.ContentType, job.ContentType), CancellationToken.None);
    }
}
