using Microsoft.EntityFrameworkCore;
using Moodle.Application.Reports.Templates;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public class ReportJobPublicationPostgresTests
{
    [Fact]
    public async Task ExpiredOrReplacedLeaseCannotPublishProgressFailOrRenewAndQuotaIsSerialized()
    {
        var connection = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection), "PostgreSQL real is required.");
        var options = new DbContextOptionsBuilder<ConnectorDbContext>().UseNpgsql(connection).Options;
        await using var db = new ConnectorDbContext(options);
        await db.ApplyVersionedSchemaAsync();
        var owner = Guid.NewGuid();
        db.UserAccounts.Add(new UserAccountEntity { Id = owner, Name = "Synthetic publication owner",
            Email = owner.ToString("N") + "@integration.test", PasswordHash = "synthetic", ConnectorClientId = "test-" + owner.ToString("N") });
        var now = DateTimeOffset.UtcNow;
        var lease = Guid.NewGuid();
        var job = new ReportJobEntity { Id = Guid.NewGuid(), OwnerId = owner, ClientId = "synthetic", ConnectionAlias = "synthetic",
            ReportType = "grades", ScopeType = "courses", Status = "running", WorkerLeaseId = lease,
            LeaseUntil = now.AddMinutes(5), RequestedAt = now, UpdatedAt = now };
        db.ReportJobs.Add(job);
        await db.SaveChangesAsync();
        try
        {
            await db.ReportJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, now.AddMinutes(-1)));
            var publisher = new ReportJobPublisher(db);
            job.FileSizeBytes = 10; job.ArtifactBlobKey = Guid.NewGuid().ToString("N");
            Assert.False(await publisher.PublishAsync(job, lease, default));
            Assert.False(await new ReportJobLeaseRenewer(new Factory(options)).RenewAsync(job.Id, lease, default));
            await Assert.ThrowsAsync<ReportDatasetException>(() => publisher.ProgressAsync(job, lease, default));
            await publisher.FailAsync(job, lease, "stale_worker");
            // Other claim tests may legitimately recover this expired lease in parallel.
            Assert.NotEqual("stale_worker", (await db.ReportJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id)).ErrorMessage);
            var newerLease = Guid.NewGuid();
            await db.ReportJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.WorkerLeaseId, newerLease)
                .SetProperty(j => j.Status, "running")
                .SetProperty(j => j.LeaseUntil, now.AddMinutes(5)));
            Assert.False(await publisher.PublishAsync(job, lease, default));
            await publisher.FailAsync(job, lease, "stale_worker");
            var running = await db.ReportJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
            Assert.Equal(newerLease, running.WorkerLeaseId);
            Assert.Null(running.ArtifactBlobKey);
            job.WorkerLeaseId = newerLease; job.LeaseUntil = now.AddMinutes(5);
            Assert.True(await publisher.PublishAsync(job, newerLease, default));
            var completed = await db.ReportJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
            Assert.Equal("completed", completed.Status);
            Assert.Null(completed.WorkerLeaseId);
            Assert.Equal(job.ArtifactBlobKey, completed.ArtifactBlobKey);
            Assert.False(await publisher.PublishAsync(job, newerLease, default));

            completed.FileSizeBytes = ReportStorageCalculator.LimitBytes - 10;
            await db.ReportJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.FileSizeBytes, completed.FileSizeBytes));
            var jobs = Enumerable.Range(0, 2).Select(_ => new ReportJobEntity { Id = Guid.NewGuid(), OwnerId = owner,
                ClientId = "synthetic", ConnectionAlias = "synthetic", ReportType = "grades", ScopeType = "courses", Status = "running",
                WorkerLeaseId = Guid.NewGuid(), LeaseUntil = now.AddMinutes(5), RequestedAt = now, UpdatedAt = now }).ToArray();
            db.ReportJobs.AddRange(jobs);
            await db.SaveChangesAsync();
            var results = await Task.WhenAll(jobs.Select(async candidate =>
            {
                await using var concurrent = new ConnectorDbContext(options);
                candidate.FileSizeBytes = 10; candidate.ArtifactBlobKey = Guid.NewGuid().ToString("N");
                try { return await new ReportJobPublisher(concurrent).PublishAsync(candidate, candidate.WorkerLeaseId!.Value, default) ? "published" : "lost"; }
                catch (ReportDatasetException e) { return e.Code; }
            }));
            Assert.Single(results, r => r == "published");
            Assert.Single(results, r => r == "report_storage_limit_exceeded");
            Assert.Equal(ReportStorageCalculator.LimitBytes, await ReportStorageCalculator.GetUsedBytesAsync(db, owner));
        }
        finally
        {
            await db.ReportJobs.Where(j => j.OwnerId == owner).ExecuteDeleteAsync();
            await db.UserAccounts.Where(a => a.Id == owner).ExecuteDeleteAsync();
        }
    }
    private sealed class Factory(DbContextOptions<ConnectorDbContext> options) : IDbContextFactory<ConnectorDbContext>
    { public ConnectorDbContext CreateDbContext() => new(options); }
}
