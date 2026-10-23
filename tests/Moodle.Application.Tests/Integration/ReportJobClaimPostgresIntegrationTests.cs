using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Reports;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class ReportJobClaimPostgresIntegrationTests
{
    [Fact]
    public async Task ConcurrentWorkersClaimAtMostOneRunningJobPerOwner()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");

        var ownerId = Guid.NewGuid();
        var clientId = $"it-client-{Guid.NewGuid():N}"[..40];
        var staleJobId = Guid.NewGuid();
        var staleWorkerLeaseId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using (var setup = new ConnectorDbContext(options))
        {
            await setup.ApplyVersionedSchemaAsync();
            setup.UserAccounts.Add(new UserAccountEntity
            {
                Id = ownerId,
                Name = "Report Claim Owner",
                Email = $"{ownerId:N}@integration.test",
                PasswordHash = "not-a-real-password",
                ConnectorClientId = clientId,
            });
            setup.ReportJobs.AddRange(Enumerable.Range(0, 2).Select(_ => new ReportJobEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                ClientId = clientId,
                ConnectionAlias = "default",
                ReportType = "grades",
                ScopeType = "course",
                Status = "queued",
                RequestedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }).Append(new ReportJobEntity
            {
                Id = staleJobId,
                OwnerId = ownerId,
                ClientId = clientId,
                ConnectionAlias = "default",
                ReportType = "grades",
                ScopeType = "course",
                Status = "running",
                WorkerLeaseId = staleWorkerLeaseId,
                LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1),
                StartedAt = DateTimeOffset.UtcNow.AddDays(-2),
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            }));
            await setup.SaveChangesAsync();
        }

        try
        {
            await using var staleWorker = new ConnectorDbContext(options);
            var staleWorkerJob = await staleWorker.ReportJobs.SingleAsync(job => job.Id == staleJobId);

            async Task<ReportJobEntity?> ClaimAsync()
            {
                await using var db = new ConnectorDbContext(options);
                return await new ReportJobClaimer(db).ClaimNextAsync(CancellationToken.None, ownerId);
            }

            var claims = await Task.WhenAll(ClaimAsync(), ClaimAsync());

            Assert.Equal(1, claims.Count(claim => claim is not null));
            Assert.Null(await ClaimAsync());

            await using var verify = new ConnectorDbContext(options);
            Assert.Equal("failed", await verify.ReportJobs
                .Where(job => job.Id == staleJobId)
                .Select(job => job.Status)
                .SingleAsync());

            staleWorkerJob.Status = "completed";
            staleWorkerJob.UpdatedAt = DateTimeOffset.UtcNow;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleWorker.SaveChangesAsync());
        }
        finally
        {
            await using var cleanup = new ConnectorDbContext(options);
            cleanup.ReportJobs.RemoveRange(await cleanup.ReportJobs.Where(job => job.OwnerId == ownerId).ToListAsync());
            cleanup.UserAccounts.RemoveRange(await cleanup.UserAccounts.Where(account => account.Id == ownerId).ToListAsync());
            await cleanup.SaveChangesAsync();
        }
    }
}
