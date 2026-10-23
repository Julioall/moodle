using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class GradingRequeueRecoveryPostgresTests
{
    [Fact]
    public async Task ConcurrentManualRequeue_RecoversAfterRestartAndOnlyOneWorkerClaimsItem()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var owner = $"requeue-recovery-{Guid.NewGuid():N}";
        var batch = AssistedGradingBatch.Create(10, [501], owner, 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.BlockAnalysis("Falha de leitura anterior.");
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.Add(item);
            await seedDb.SaveChangesAsync();
        }

        async Task<RequeueBlockedGradingItemsResult> RequeueAsync()
        {
            await using var db = new ConnectorDbContext(options);
            var handler = new RequeueBlockedGradingItemsCommandHandler(
                new GradingReviewRepository(db),
                new TestCurrentUserContext(owner));
            return await handler.Handle(
                new RequeueBlockedGradingItemsCommand(batch.Id, [item.Id]),
                CancellationToken.None);
        }

        var requeueResults = await Task.WhenAll(RequeueAsync(), RequeueAsync());
        Assert.All(requeueResults, result => Assert.Equal("wait", result.NextAction));
        Assert.Contains(requeueResults, result => result.RequeuedItems == 1);

        // A fresh context models the worker process starting after the
        // requeue request has committed.
        await using (var restartedDb = new ConnectorDbContext(options))
        {
            var persisted = await restartedDb.GradingItems
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == item.Id);
            Assert.Equal(GradingItemStatus.Pending, persisted.Status);
            Assert.Equal(0, persisted.AttemptCount);
        }

        var claimTime = DateTimeOffset.UtcNow;
        async Task<GradingItemLeaseClaim?> ClaimAsync(string workerId)
        {
            await using var db = new ConnectorDbContext(options);
            return await new GradingReviewRepository(db).TryClaimItemAsync(
                batch.Id,
                item.Id,
                workerId,
                claimTime,
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
        }

        var claims = await Task.WhenAll(
            ClaimAsync("recovery-worker-a"),
            ClaimAsync("recovery-worker-b"));
        var winner = Assert.Single(claims, claim => claim is not null)!;
        Assert.Equal(1, claims.Count(claim => claim is null));
        Assert.Equal(1, winner.AttemptCount);
    }

    [Fact]
    public async Task ExpiredWorkerCannotPersistStaleItemResultAfterAnotherWorkerReclaimsIt()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var owner = $"lease-fence-{Guid.NewGuid():N}";
        var batch = AssistedGradingBatch.Create(10, [501], owner, 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.Add(item);
            await seedDb.SaveChangesAsync();
        }

        var expiredLeaseStart = DateTimeOffset.UtcNow.AddMinutes(-20);
        var leaseDuration = TimeSpan.FromMinutes(10);
        await using var staleWorkerDb = new ConnectorDbContext(options);
        var staleWorkerRepository = new GradingReviewRepository(staleWorkerDb);
        Assert.NotNull(await staleWorkerRepository.TryClaimBatchAsync(
            batch.Id,
            "stale-worker",
            expiredLeaseStart,
            leaseDuration,
            CancellationToken.None));
        Assert.NotNull(await staleWorkerRepository.TryClaimItemAsync(
            batch.Id,
            item.Id,
            "stale-worker",
            expiredLeaseStart,
            leaseDuration,
            CancellationToken.None));

        var staleTrackedItem = await staleWorkerDb.GradingItems.SingleAsync(candidate => candidate.Id == item.Id);
        staleTrackedItem.MarkAnalysisFailed("synthetic stale worker result");

        var reclaimTime = DateTimeOffset.UtcNow;
        await using (var recoveryDb = new ConnectorDbContext(options))
        {
            var recoveryStore = new GradingReviewRepository(recoveryDb);
            await recoveryStore.RecoverExpiredBatchLeasesAsync(reclaimTime, CancellationToken.None);
            await recoveryStore.RecoverExpiredItemLeasesAsync(reclaimTime, CancellationToken.None);
            Assert.NotNull(await recoveryStore.TryClaimBatchAsync(
                batch.Id,
                "current-worker",
                reclaimTime,
                leaseDuration,
                CancellationToken.None));
            Assert.NotNull(await recoveryStore.TryClaimItemAsync(
                batch.Id,
                item.Id,
                "current-worker",
                reclaimTime,
                leaseDuration,
                CancellationToken.None));
            var recoveredLeaseOwner = await recoveryDb.GradingItems
                .AsNoTracking()
                .Where(candidate => candidate.Id == item.Id)
                .Select(candidate => candidate.LeaseOwner)
                .SingleAsync();
            Assert.Equal("current-worker", recoveredLeaseOwner);
        }

        Assert.False(await staleWorkerRepository.SaveChangesWithActiveLeasesAsync(
            batch.Id,
            "stale-worker",
            [item.Id],
            DateTimeOffset.UtcNow,
            leaseDuration,
            CancellationToken.None));

        await using var verifyDb = new ConnectorDbContext(options);
        var persisted = await verifyDb.GradingItems
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == item.Id);
        Assert.Equal(GradingItemStatus.Pending, persisted.Status);
        Assert.Equal("current-worker", persisted.LeaseOwner);
        Assert.Equal(2, persisted.AttemptCount);
    }

    private static string GetConnectionStringOrFail()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "PostgreSQL integration tests must run through scripts/test-postgres.ps1; the database gate cannot silently skip.");
        return connectionString!;
    }

    private sealed class TestCurrentUserContext(string subject) : ICurrentUserContext
    {
        public string Subject => subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }
}
