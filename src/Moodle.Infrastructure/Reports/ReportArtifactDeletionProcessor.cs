using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure.Reports;

internal sealed class ReportArtifactDeletionProcessor(
    ConnectorDbContext dbContext,
    IReportArtifactStore artifactStore,
    TimeProvider timeProvider)
{
    private const int BatchSize = 32;
    private const int MaximumRetryDelaySeconds = 6 * 60 * 60;

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var requests = await dbContext.ReportArtifactDeletions
            .Where(item => item.NextAttemptAt <= now)
            .OrderBy(item => item.NextAttemptAt)
            .ThenBy(item => item.RequestedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var completed = 0;

        foreach (var request in requests)
        {
            try
            {
                await artifactStore.DeleteAsync(request.ArtifactBlobKey, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (request.AttemptCount < int.MaxValue)
                {
                    request.AttemptCount++;
                }
                request.LastError = $"Artifact deletion failed ({exception.GetType().Name}).";
                request.NextAttemptAt = now.Add(GetRetryDelay(request.AttemptCount));
                await dbContext.SaveChangesAsync(CancellationToken.None);
                continue;
            }

            dbContext.ReportArtifactDeletions.Remove(request);
            await dbContext.SaveChangesAsync(cancellationToken);
            completed++;
        }

        return completed;
    }

    private static TimeSpan GetRetryDelay(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 16);
        var seconds = Math.Min(MaximumRetryDelaySeconds, 5d * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}
