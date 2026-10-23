using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class ReportArtifactDeletionProcessorTests
{
    [Fact]
    public async Task FailedDeleteIsRetriedAndSuccessfulDeleteConsumesRequest()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"report-artifact-delete-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ConnectorDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var failedKey = Guid.NewGuid().ToString("N");
        var successfulKey = Guid.NewGuid().ToString("N");
        db.ReportArtifactDeletions.AddRange(
            new ReportArtifactDeletionEntity
            {
                Id = Guid.NewGuid(),
                ArtifactBlobKey = failedKey,
                RequestedAt = now.AddMinutes(-1),
                NextAttemptAt = now.AddSeconds(-1),
            },
            new ReportArtifactDeletionEntity
            {
                Id = Guid.NewGuid(),
                ArtifactBlobKey = successfulKey,
                RequestedAt = now.AddMinutes(-1),
                NextAttemptAt = now.AddSeconds(-1),
            });
        await db.SaveChangesAsync();
        var timeProvider = new TestTimeProvider(now);
        var store = new FailingOnceReportArtifactStore(failedKey);
        var processor = new ReportArtifactDeletionProcessor(db, store, timeProvider);

        await processor.ProcessDueAsync(CancellationToken.None);

        var failedRequest = await db.ReportArtifactDeletions.SingleAsync();
        Assert.Equal(failedKey, failedRequest.ArtifactBlobKey);
        Assert.Equal(1, failedRequest.AttemptCount);
        Assert.Equal(now.AddSeconds(5), failedRequest.NextAttemptAt);
        Assert.DoesNotContain("sensitive-path", failedRequest.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([failedKey, successfulKey], store.DeleteCalls);

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await processor.ProcessDueAsync(CancellationToken.None);

        Assert.Empty(await db.ReportArtifactDeletions.ToListAsync());
        Assert.Equal([failedKey, successfulKey, failedKey], store.DeleteCalls);
    }

    private sealed class FailingOnceReportArtifactStore(string failOnceFor) : IReportArtifactStore
    {
        private int remainingFailures = 1;

        public List<string> DeleteCalls { get; } = [];

        public Task WriteAsync(string blobKey, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken)
        {
            DeleteCalls.Add(blobKey);
            if (blobKey == failOnceFor && Interlocked.Exchange(ref remainingFailures, 0) == 1)
            {
                throw new IOException("sensitive-path must not be persisted");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }
}
