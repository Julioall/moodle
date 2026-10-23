using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class GradingPublicationRecoveryPostgresTests
{
    [Fact]
    public async Task RecoveredLease_StaleExecutorCannotRenewOrExtendNewOwnersLease()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail())
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var now = DateTimeOffset.UtcNow;
        var leaseDuration = TimeSpan.FromMinutes(5);
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            CreatedBySubject = $"lease-recovery-{Guid.NewGuid():N}",
            PayloadJson = "{}",
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR_PUBLICACAO",
            ExpiresAt = now.AddHours(1),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        action.Authorize(action.CreatedBySubject, now);
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.PendingMoodleActions.Add(action);
            await seedDb.SaveChangesAsync();
        }

        await using var staleExecutorDb = new ConnectorDbContext(options);
        var staleExecutor = new PendingMoodleActionRepository(staleExecutorDb);
        var initialClaim = await staleExecutor.TryBeginExecutionAsync(
            action.Id,
            "worker-a",
            now,
            leaseDuration,
            CancellationToken.None);
        Assert.True(initialClaim.Claimed);
        var staleTrackedAction = await staleExecutor.GetByIdAsync(action.Id, CancellationToken.None);
        Assert.Equal("worker-a", staleTrackedAction!.ExecutionOwner);

        await using (var competingDb = new ConnectorDbContext(options))
        {
            var competing = await new PendingMoodleActionRepository(competingDb).TryBeginExecutionAsync(
                action.Id,
                "worker-b-too-early",
                now.AddMinutes(1),
                leaseDuration,
                CancellationToken.None);
            Assert.False(competing.Claimed);
        }

        var recoveryAt = now.Add(leaseDuration).AddSeconds(1);
        await using (var recoveryDb = new ConnectorDbContext(options))
        {
            var recovered = await new PendingMoodleActionRepository(recoveryDb).TryBeginExecutionAsync(
                action.Id,
                "worker-b",
                recoveryAt,
                leaseDuration,
                CancellationToken.None);
            Assert.True(recovered.Claimed);
            Assert.Equal(2, recovered.AttemptCount);
        }

        var staleRenewal = await staleExecutor.TryRenewExecutionLeaseAsync(
            action.Id,
            "worker-a",
            recoveryAt.AddSeconds(1),
            leaseDuration,
            CancellationToken.None);
        Assert.False(staleRenewal);

        await using var verifyDb = new ConnectorDbContext(options);
        var persisted = await verifyDb.PendingMoodleActions.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == action.Id);
        Assert.Equal(PendingActionStatus.Executing, persisted.Status);
        Assert.Equal("worker-b", persisted.ExecutionOwner);
        var expectedLeaseUntil = recoveryAt.Add(leaseDuration);
        Assert.InRange(
            persisted.ExecutionLeaseUntil!.Value,
            expectedLeaseUntil.AddTicks(-10),
            expectedLeaseUntil.AddTicks(10));
    }

    [Fact]
    public async Task PublicationCheckpoint_RejectsStaleFencingGenerationWithoutChangingItemClaimOrLedger()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail())
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var subject = $"fencing-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var batch = AssistedGradingBatch.Create(10, [501], subject, 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.ApplyTeacherReview(8m, "Feedback de teste.", subject, 321);
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            CreatedBySubject = subject,
            PayloadJson = "{}",
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR_PUBLICACAO",
            ExpiresAt = now.AddHours(1),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        action.Authorize(subject, now);
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.Add(item);
            seedDb.PendingMoodleActions.Add(action);
            await seedDb.SaveChangesAsync();
        }

        var publicationId = Guid.NewGuid();
        await using (var claimDb = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(claimDb);
            var claimed = await repository.TryClaimPublicationTargetsAsync(
                publicationId,
                $"connection-test-{Guid.NewGuid():N}",
                [new GradingPublicationClaimRequest(item.Id, 501, 9001, 0)],
                now.AddHours(1),
                CancellationToken.None);
            Assert.True(Assert.Single(claimed).Claimed);
            await repository.BindPublicationClaimsAsync(publicationId, action.Id, CancellationToken.None);
        }

        const string workerA = "worker-a";
        const string workerB = "worker-b";
        var leaseDuration = TimeSpan.FromMinutes(5);
        long fencingTokenA;
        await using (var workerADb = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(workerADb);
            var actionRepository = new PendingMoodleActionRepository(workerADb);
            var claimA = await actionRepository.TryBeginExecutionAsync(
                action.Id,
                workerA,
                now,
                leaseDuration,
                CancellationToken.None);
            Assert.True(claimA.Claimed);
            fencingTokenA = claimA.FencingToken;
            await repository.ActivatePublicationClaimsAsync(
                publicationId,
                action.Id,
                workerA,
                fencingTokenA,
                CancellationToken.None);

            var trackedItem = await repository.GetItemAsync(item.Id, CancellationToken.None);
            Assert.NotNull(trackedItem);
            var intentAt = DateTimeOffset.UtcNow;
            Assert.True(trackedItem!.TryMarkCommitWriteIntent(intentAt));
            await repository.SavePublicationCheckpointAsync(
                "Executing",
                new GradingPublicationAttemptRecord(
                    action.Id,
                    publicationId,
                    item.Id,
                    SubmissionAttemptNumber: 0,
                    DispatchAttemptNumber: 1,
                    EventType: "write_intent_persisted",
                    ItemState: "Pending",
                    WorkerId: workerA,
                    FencingToken: fencingTokenA,
                    ErrorCode: null,
                    EvidenceKind: "pre_post_intent",
                    RemoteObservedAtUtc: null,
                    CreatedAtUtc: intentAt),
                CancellationToken.None);
        }

        long fencingTokenB;
        await using (var workerBDb = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(workerBDb);
            var recoveredAt = now.AddMinutes(6);
            var claimB = await new PendingMoodleActionRepository(workerBDb).TryBeginExecutionAsync(
                action.Id,
                workerB,
                recoveredAt,
                leaseDuration,
                CancellationToken.None);
            Assert.True(claimB.Claimed);
            fencingTokenB = claimB.FencingToken;
            Assert.True(fencingTokenB > fencingTokenA);
            await repository.ActivatePublicationClaimsAsync(
                publicationId,
                action.Id,
                workerB,
                fencingTokenB,
                CancellationToken.None);
        }

        await using (var staleWorkerDb = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(staleWorkerDb);
            var trackedItem = await repository.GetItemAsync(item.Id, CancellationToken.None);
            Assert.NotNull(trackedItem);
            trackedItem!.MarkCommitExecutionUnknown("worker expirado");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SavePublicationCheckpointAsync(
                    "ExecutionUnknown",
                    new GradingPublicationAttemptRecord(
                        action.Id,
                        publicationId,
                        item.Id,
                        SubmissionAttemptNumber: 0,
                        DispatchAttemptNumber: 1,
                        EventType: "stale_worker_checkpoint",
                        ItemState: "ExecutionUnknown",
                        WorkerId: workerA,
                        FencingToken: fencingTokenA,
                        ErrorCode: "stale_worker",
                        EvidenceKind: "stale_checkpoint",
                        RemoteObservedAtUtc: null,
                        CreatedAtUtc: DateTimeOffset.UtcNow),
                    CancellationToken.None));
            Assert.Contains("fencing token", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using var verifyDb = new ConnectorDbContext(options);
        var persistedItem = await verifyDb.GradingItems.AsNoTracking().SingleAsync(candidate => candidate.Id == item.Id);
        var persistedClaim = await verifyDb.GradingPublicationClaims.AsNoTracking()
            .SingleAsync(candidate => candidate.PublicationId == publicationId && candidate.GradingItemId == item.Id);
        var events = await verifyDb.GradingPublicationAttemptEvents.AsNoTracking()
            .Where(candidate => candidate.PendingActionId == action.Id && candidate.GradingItemId == item.Id)
            .ToArrayAsync();

        Assert.Equal(GradingCommitStatus.Pending, persistedItem.CommitStatus);
        Assert.NotNull(persistedItem.CommitWriteIntentAt);
        Assert.Equal("Executing", persistedClaim.Status);
        Assert.Equal(workerB, persistedClaim.WorkerId);
        Assert.Equal(fencingTokenB, persistedClaim.FencingToken);
        Assert.Collection(events, attempt => Assert.Equal("write_intent_persisted", attempt.EventType));
    }

    private static string GetConnectionStringOrFail()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "PostgreSQL integration tests must run through scripts/test-postgres.ps1; the database gate cannot silently skip.");
        return connectionString!;
    }
}
