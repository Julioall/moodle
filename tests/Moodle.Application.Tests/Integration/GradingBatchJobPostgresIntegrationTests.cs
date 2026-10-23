using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Npgsql;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class GradingBatchJobPostgresIntegrationTests
{
    [Fact]
    public async Task PurgeCancelledBatch_LocksPackageUntilDeletionCommits()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail()).Options;
        var owner = $"purge-lock-{Guid.NewGuid():N}";
        var batch = AssistedGradingBatch.Create(10, [501], owner, 321, totalItems: 0);
        batch.Cancel();
        var package = GradingEvidencePackage.Create(owner, owner, null, "default", 10, 501,
            "501", 1, new string('a', 64), new string('b', 64), batch.Id);
        await using (var seed = new ConnectorDbContext(options))
        {
            await seed.ApplyVersionedSchemaAsync();
            seed.GradingBatches.Add(batch);
            seed.GradingEvidencePackages.Add(package);
            await seed.SaveChangesAsync();
        }

        var barrier = new PausePackageDeletionInterceptor();
        var purgeOptions = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail()).AddInterceptors(barrier).Options;
        await using var purgeDb = new ConnectorDbContext(purgeOptions);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var purge = new GradingReviewRepository(purgeDb).PurgeCancelledGradingDataAsync(
            [batch.Id], null, timeout.Token);
        try
        {
            // Pause after the guard but before the first DELETE, so only the
            // explicit FOR UPDATE can prevent a worker from acquiring a lease.
            await barrier.Reached.Task.WaitAsync(timeout.Token);
            await using var claimDb = new ConnectorDbContext(options);
            await claimDb.Database.OpenConnectionAsync(timeout.Token);
            await claimDb.Database.ExecuteSqlRawAsync("SET lock_timeout = '300ms'", timeout.Token);
            var exception = await Record.ExceptionAsync(() =>
                new GradingPackageStore(claimDb).TryClaimAsync(package.Id, "racing-worker",
                    DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), timeout.Token));
            Assert.NotNull(exception);
            var postgresException = Assert.IsType<PostgresException>(exception!.GetBaseException());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, postgresException.SqlState);
        }
        finally
        {
            barrier.Resume.TrySetResult();
            await purge;
        }

        Assert.True((await purge).Purged);
        await using var verify = new ConnectorDbContext(options);
        Assert.False(await new GradingPackageStore(verify).TryClaimAsync(package.Id, "late-worker",
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), timeout.Token));
        Assert.False(await verify.GradingEvidencePackages.AnyAsync(x => x.Id == package.Id, timeout.Token));
    }

    [Fact]
    public async Task GetBatchesAsync_UsesOnePostgresReaderCommandForManyIds()
    {
        var counter = new SelectCommandCounter();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail())
            .AddInterceptors(counter)
            .Options;
        await using var db = new ConnectorDbContext(options);
        await db.ApplyVersionedSchemaAsync();
        var owner = $"batch-lookup-{Guid.NewGuid():N}";
        var batches = Enumerable.Range(0, 20)
            .Select(_ => AssistedGradingBatch.Create(10, [501], owner, 321, 1))
            .ToArray();
        db.GradingBatches.AddRange(batches);
        await db.SaveChangesAsync();
        counter.Reset();

        var result = await new GradingReviewRepository(db).GetBatchesAsync(
            batches.Select(batch => batch.Id).Append(batches[0].Id).ToArray(),
            CancellationToken.None);

        Assert.Equal(20, result.Count);
        Assert.Equal(1, counter.SelectCommandCount);
    }

    private sealed class SelectCommandCounter : DbCommandInterceptor
    {
        private int _selectCommandCount;

        public int SelectCommandCount => Volatile.Read(ref _selectCommandCount);

        public void Reset() => Interlocked.Exchange(ref _selectCommandCount, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _selectCommandCount);
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class PausePackageDeletionInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("grading_evidence_package_file", StringComparison.Ordinal))
            {
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("anchor", "member", false, false)]
    [InlineData("member", "member", false, false)]
    [InlineData("file", "file", false, false)]
    [InlineData("anchor", "file", false, false)]
    [InlineData("anchor", "none", true, false)]
    [InlineData("member", "none", true, false)]
    [InlineData("file", "none", true, false)]
    [InlineData("anchor", "none", false, false)]
    [InlineData("member", "member", false, true)]
    public async Task PurgeCancelledBatch_ProtectsPackageScopeAndLease(
        string candidate, string outside, bool activeLease, bool fullRun)
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail()).Options;
        await using var db = new ConnectorDbContext(options);
        await db.ApplyVersionedSchemaAsync();
        await Moodle.Application.Tests.Infrastructure.GradingReviewRepositoryTests
            .VerifyPackagePurgeGuardAsync(db, candidate, outside, activeLease, fullRun);
    }

    [Fact]
    public async Task DurableAction_ConcurrentWorkersHaveOneLease_AndExpiredLeaseCanResume()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            CreatedBySubject = $"integration-action-{Guid.NewGuid():N}",
            PayloadJson = "{}",
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR_PUBLICACAO",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.PendingMoodleActions.Add(action);
            await seedDb.SaveChangesAsync();
        }

        var now = DateTimeOffset.UtcNow;
        await using (var authorizeDb = new ConnectorDbContext(options))
        {
            var repository = new PendingMoodleActionRepository(authorizeDb);
            var claim = await repository.TryAuthorizeWithAuditAsync(
                action.Id,
                action.CreatedBySubject,
                now,
                new MoodleAuditLog
                {
                    PendingActionId = action.Id,
                    CorrelationId = action.CorrelationId,
                    ToolName = action.ToolName,
                    RiskLevel = action.RiskLevel,
                    ActorSubject = action.CreatedBySubject,
                    RequestSanitizedJson = "{}",
                    ResponseSummaryJson = "{}",
                    Status = "authorized"
                },
                CancellationToken.None);
            Assert.True(claim.ConfirmedByCaller);
            Assert.Equal(PendingActionStatus.Authorized, claim.Status);
        }

        async Task<PendingActionExecutionClaimResult> BeginAsync(string workerId)
        {
            await using var db = new ConnectorDbContext(options);
            var repository = new PendingMoodleActionRepository(db);
            return await repository.TryBeginExecutionAsync(
                action.Id,
                workerId,
                now,
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
        }

        var firstClaims = await Task.WhenAll(BeginAsync("action-worker-a"), BeginAsync("action-worker-b"));
        Assert.Single(firstClaims, claim => claim.Claimed);
        Assert.Single(firstClaims, claim => !claim.Claimed);

        await using var recoveryDb = new ConnectorDbContext(options);
        var recoveryRepository = new PendingMoodleActionRepository(recoveryDb);
        var recovered = await recoveryRepository.TryBeginExecutionAsync(
            action.Id,
            "action-worker-c",
            now.AddMinutes(11),
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        Assert.True(recovered.Claimed);
        Assert.Equal(2, recovered.AttemptCount);
    }

    [Fact]
    public async Task PurgeCancelledBatch_RemovesPackageProjectionBeforeItems()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var owner = $"integration-purge-{Guid.NewGuid():N}";
        var run = GradingRun.Create(owner);
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            owner,
            321,
            totalItems: 1,
            gradingRunId: run.Id,
            connectorClientId: owner,
            connectionAlias: "default",
            moodleConnectionId: $"connection-{owner}");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var artifactId = Guid.NewGuid();
        var artifactHash = new string('a', 64);
        var artifact = new GradingArtifact(
            artifactId,
            item.Id,
            "submission_file",
            "answer.txt",
            "text/plain",
            artifactHash,
            10,
            ExtractionStatus.Succeeded,
            null,
            null,
            DateTimeOffset.UtcNow);
        var package = GradingEvidencePackage.Create(
            owner,
            $"connection-{owner}",
            owner,
            "default",
            10,
            501,
            "1001",
            1,
            new string('b', 64),
            new string('c', 64),
            batch.Id);
        var member = GradingEvidencePackageMember.Create(
            package.Id,
            item.Id,
            101,
            "Aluno 101",
            9001,
            0,
            DateTimeOffset.UtcNow,
            null,
            null);
        var file = GradingEvidencePackageFile.Create(
            package.Id,
            item.Id,
            artifactId,
            "submissions/aluno-101/answer.txt",
            "answer.txt",
            "text/plain",
            artifactHash,
            10,
            false);

        batch.Cancel();
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingRuns.Add(run);
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.Add(item);
            seedDb.GradingArtifacts.Add(artifact);
            seedDb.GradingEvidencePackages.Add(package);
            seedDb.GradingEvidencePackageMembers.Add(member);
            seedDb.GradingEvidencePackageFiles.Add(file);
            await seedDb.SaveChangesAsync();
        }

        await using (var purgeDb = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(purgeDb);
            var result = await repository.PurgeCancelledGradingDataAsync(
                [batch.Id],
                run.Id,
                CancellationToken.None);

            Assert.True(result.Purged);
            Assert.Equal(1, result.DeletedItems);
            Assert.Equal(1, result.DeletedBatches);
            Assert.Equal(1, result.DeletedRuns);
        }

        await using (var verifyDb = new ConnectorDbContext(options))
        {
            Assert.False(await verifyDb.GradingItems.AnyAsync(itemEntry => itemEntry.Id == item.Id));
            Assert.False(await verifyDb.GradingEvidencePackageMembers.AnyAsync(entry => entry.Id == member.Id));
            Assert.False(await verifyDb.GradingEvidencePackageFiles.AnyAsync(entry => entry.Id == file.Id));
            Assert.False(await verifyDb.GradingEvidencePackages.AnyAsync(entry => entry.Id == package.Id));
        }
    }

    [Fact]
    public async Task ConcurrentPublicationClaims_OnlyOneWorkerOwnsTheSameMoodleTarget()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var connectionKey = $"integration-claim-{Guid.NewGuid():N}";
        var request = new GradingPublicationClaimRequest(
            Guid.NewGuid(),
            AssignmentId: 501,
            MoodleUserId: 9001,
            AttemptNumber: 2);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

        async Task<GradingPublicationClaimResult> ClaimAsync(Guid publicationId)
        {
            await using var db = new ConnectorDbContext(options);
            var store = new GradingReviewRepository(db);
            return Assert.Single(await store.TryClaimPublicationTargetsAsync(
                publicationId,
                connectionKey,
                [request],
                expiresAt,
                CancellationToken.None));
        }

        var results = await Task.WhenAll(ClaimAsync(Guid.NewGuid()), ClaimAsync(Guid.NewGuid()));
        Assert.Single(results, result => result.Claimed);
        Assert.Single(results, result => !result.Claimed);

        await using var verifyDb = new ConnectorDbContext(options);
        var persisted = await verifyDb.GradingPublicationClaims
            .Where(claim => claim.ConnectionKey == connectionKey)
            .ToArrayAsync();
        Assert.Single(persisted);
        Assert.Equal("AwaitingConfirmation", persisted[0].Status);
    }

    [Fact]
    public async Task ExpiredPreviewClaim_IsReleasedBeforeTheSameTargetIsClaimedAgain()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var connectionKey = $"integration-expired-claim-{Guid.NewGuid():N}";
        var request = new GradingPublicationClaimRequest(Guid.NewGuid(), 777, 8888, 1);
        await using (var firstDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(firstDb);
            var first = await store.TryClaimPublicationTargetsAsync(
                Guid.NewGuid(),
                connectionKey,
                [request],
                DateTimeOffset.UtcNow.AddMinutes(-1),
                CancellationToken.None);
            Assert.True(Assert.Single(first).Claimed);
        }

        await using (var secondDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(secondDb);
            var second = await store.TryClaimPublicationTargetsAsync(
                Guid.NewGuid(),
                connectionKey,
                [request with { GradingItemId = Guid.NewGuid() }],
                DateTimeOffset.UtcNow.AddMinutes(15),
                CancellationToken.None);
            Assert.True(Assert.Single(second).Claimed);
        }

        await using var verifyDb = new ConnectorDbContext(options);
        var claims = await verifyDb.GradingPublicationClaims
            .Where(claim => claim.ConnectionKey == connectionKey)
            .OrderBy(claim => claim.CreatedAt)
            .ToArrayAsync();
        Assert.Equal(2, claims.Length);
        Assert.Equal("Released", claims[0].Status);
        Assert.Equal("AwaitingConfirmation", claims[1].Status);
    }

    [Fact]
    public async Task AuthorizedPublicationClaim_RemainsExclusiveAfterPreviewExpiry()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var connectionKey = $"integration-authorized-claim-{Guid.NewGuid():N}";
        var publicationId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            CreatedBySubject = $"integration-authorized-{Guid.NewGuid():N}",
            PayloadJson = "{}",
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR_PUBLICACAO",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.PendingMoodleActions.Add(action);
            seedDb.GradingPublicationClaims.Add(new GradingPublicationClaimEntity
            {
                PublicationId = publicationId,
                GradingItemId = itemId,
                ConnectionKey = connectionKey,
                AssignmentId = 501,
                MoodleUserId = 9001,
                AttemptNumber = 1,
                Status = "AwaitingConfirmation",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            await seedDb.SaveChangesAsync();
        }

        await using (var bindDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(bindDb);
            await store.BindPublicationClaimsAsync(publicationId, action.Id, CancellationToken.None);
            var trackedAction = await bindDb.PendingMoodleActions.SingleAsync(item => item.Id == action.Id);
            trackedAction.Authorize(action.CreatedBySubject, DateTimeOffset.UtcNow);
            await bindDb.SaveChangesAsync();
        }

        await using var verifyDb = new ConnectorDbContext(options);
        var verifyStore = new GradingReviewRepository(verifyDb);
        var result = await verifyStore.TryClaimPublicationTargetsAsync(
            Guid.NewGuid(),
            connectionKey,
            [new GradingPublicationClaimRequest(Guid.NewGuid(), 501, 9001, 1)],
            DateTimeOffset.UtcNow.AddMinutes(15),
            CancellationToken.None);

        Assert.False(Assert.Single(result).Claimed);
        var claim = await verifyDb.GradingPublicationClaims.SingleAsync(item => item.PublicationId == publicationId);
        Assert.Equal("AwaitingConfirmation", claim.Status);
    }

    [Fact]
    public async Task ConcurrentRunDestination_OnlyOneIntentWins()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var run = GradingRun.Create($"integration-destination-{Guid.NewGuid():N}");
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingRuns.Add(run);
            await seedDb.SaveChangesAsync();
        }

        async Task<bool> SetDestinationAsync(string destination)
        {
            await using var db = new ConnectorDbContext(options);
            var store = new GradingReviewRepository(db);
            return await store.TrySetGradingRunDestinationAsync(
                run.Id,
                destination,
                CancellationToken.None);
        }

        var results = await Task.WhenAll(
            SetDestinationAsync("csv"),
            SetDestinationAsync("publish"));

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);

        await using var verifyDb = new ConnectorDbContext(options);
        var persisted = await verifyDb.GradingRuns.SingleAsync(candidate => candidate.Id == run.Id);
        Assert.Contains(persisted.Destination, new[] { "csv", "publish" });
    }

    [Fact]
    public async Task ConcurrentClaims_OnlyOneWorkerAcquiresLease_AndExpiredLeaseIsRecoverable()
    {
        var connectionString = GetConnectionStringOrFail();

        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(
            courseId: 10,
            assignmentIds: [501],
            createdBySubject: $"integration-{Guid.NewGuid():N}",
            createdByMoodleUserId: 321,
            totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.Add(item);
            await seedDb.SaveChangesAsync();
        }

        var claimTime = DateTimeOffset.UtcNow;
        async Task<GradingBatchLeaseClaim?> ClaimAsync(string workerId)
        {
            await using var db = new ConnectorDbContext(options);
            var store = new GradingReviewRepository(db);
            return await store.TryClaimBatchAsync(
                batch.Id,
                workerId,
                claimTime,
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
        }

        var claims = await Task.WhenAll(ClaimAsync("worker-a"), ClaimAsync("worker-b"));
        var winner = Assert.Single(claims, claim => claim is not null)!;
        Assert.Single(claims, claim => claim is null);
        await using (var verifyDb = new ConnectorDbContext(options))
        {
            var persisted = await verifyDb.GradingBatches.SingleAsync(item => item.Id == batch.Id);
            Assert.Equal(winner.WorkerId, persisted.LeaseOwner);
            Assert.Equal(1, persisted.AttemptCount);
        }

        await using (var expiredDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(expiredDb);
            var recovered = await store.RecoverExpiredBatchLeasesAsync(
                claimTime.AddMinutes(11),
                CancellationToken.None);
            Assert.True(recovered >= 1);
        }

        await using (var recoveredDb = new ConnectorDbContext(options))
        {
            var recoveredBatch = await recoveredDb.GradingBatches
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == batch.Id);
            Assert.Null(recoveredBatch.LeaseOwner);
            Assert.Null(recoveredBatch.LeaseUntil);

            var store = new GradingReviewRepository(recoveredDb);
            var retry = await store.TryClaimBatchAsync(
                batch.Id,
                "worker-c",
                claimTime.AddMinutes(11),
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
            Assert.NotNull(retry);
            Assert.Equal(2, retry!.AttemptCount);
        }
    }

    [Fact]
    public async Task ConcurrentItemClaims_OnlyOneWorkerAcquiresLease_AndRetryCountsAttempt()
    {
        var connectionString = GetConnectionStringOrFail();

        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(
            courseId: 10,
            assignmentIds: [501],
            createdBySubject: $"integration-item-{Guid.NewGuid():N}",
            createdByMoodleUserId: 321,
            totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.Add(item);
            await seedDb.SaveChangesAsync();
        }

        var claimTime = DateTimeOffset.UtcNow;
        async Task<GradingItemLeaseClaim?> ClaimAsync(string workerId)
        {
            await using var db = new ConnectorDbContext(options);
            var store = new GradingReviewRepository(db);
            return await store.TryClaimItemAsync(
                batch.Id,
                item.Id,
                workerId,
                claimTime,
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
        }

        // Simulate a fan-out of replicas/users contending for the same item.
        // The conditional UPDATE must yield exactly one winner even under
        // ten concurrent transactions.
        var claims = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(index => ClaimAsync($"item-worker-{index}")));
        var winner = Assert.Single(claims, claim => claim is not null)!;
        Assert.Equal(9, claims.Count(claim => claim is null));
        Assert.Equal(1, winner.AttemptCount);

        await using (var expiredDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(expiredDb);
            var recovered = await store.RecoverExpiredItemLeasesAsync(
                claimTime.AddMinutes(11),
                CancellationToken.None);
            Assert.True(recovered >= 1);
        }

        await using (var verifyRecoveryDb = new ConnectorDbContext(options))
        {
            var recoveredItem = await verifyRecoveryDb.GradingItems
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == item.Id);
            Assert.Null(recoveredItem.LeaseOwner);
            Assert.Null(recoveredItem.LeaseUntil);
        }

        await using (var retryDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(retryDb);
            var retry = await store.TryClaimItemAsync(
                batch.Id,
                item.Id,
                "item-worker-c",
                claimTime.AddMinutes(11),
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
            Assert.NotNull(retry);
            Assert.Equal(2, retry!.AttemptCount);
        }
    }

    [Fact]
    public async Task BulkItemClaims_RenewAndReleaseRemainExclusiveForAWindow()
    {
        var connectionString = GetConnectionStringOrFail();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(
            courseId: 10,
            assignmentIds: [501],
            createdBySubject: $"integration-bulk-item-{Guid.NewGuid():N}",
            createdByMoodleUserId: 321,
            totalItems: 32);
        var items = Enumerable.Range(0, 32)
            .Select(index => AssistedGradingItem.Create(
                batch.Id,
                10,
                501,
                9001 + index,
                101 + index,
                0))
            .ToArray();
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.AddRange(items);
            await seedDb.SaveChangesAsync();
        }

        var itemIds = items.Select(item => item.Id).ToArray();
        var claimTime = DateTimeOffset.UtcNow;
        await using (var firstDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(firstDb);
            var claimed = await store.TryClaimItemsAsync(
                batch.Id,
                itemIds,
                "bulk-worker-a",
                claimTime,
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
            Assert.Equal(32, claimed.Count);

            var renewed = await store.RenewItemLeasesAsync(
                batch.Id,
                claimed,
                "bulk-worker-a",
                claimTime.AddMinutes(1),
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
            Assert.Equal(32, renewed);

            var competingClaim = await store.TryClaimItemsAsync(
                batch.Id,
                itemIds,
                "bulk-worker-b",
                claimTime.AddMinutes(2),
                TimeSpan.FromMinutes(10),
                CancellationToken.None);
            Assert.Empty(competingClaim);

            var released = await store.ReleaseItemLeasesAsync(
                batch.Id,
                claimed,
                "bulk-worker-a",
                claimTime.AddMinutes(3),
                errorCode: null,
                nextAttemptAt: null,
                CancellationToken.None);
            Assert.Equal(32, released);
        }

        await using var verifyDb = new ConnectorDbContext(options);
        var persisted = await verifyDb.GradingItems
            .AsNoTracking()
            .Where(item => item.BatchId == batch.Id)
            .ToArrayAsync();
        Assert.Equal(32, persisted.Length);
        Assert.All(persisted, item =>
        {
            Assert.Null(item.LeaseOwner);
            Assert.Null(item.LeaseUntil);
            Assert.Equal(1, item.AttemptCount);
        });
    }

    [Fact]
    public async Task VersionedProposal_PersistsJsonbAndIsIdempotent()
    {
        var connectionString = GetConnectionStringOrFail();

        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(10, [501], $"integration-proposal-{Guid.NewGuid():N}", 321, 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.Add(item);
            await seedDb.SaveChangesAsync();
        }

        await using (var proposalDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(proposalDb);
            var proposal = AiGradingProposal.Create(
                item.Id,
                batch.Id,
                await store.GetNextVersionAsync(item.Id, CancellationToken.None),
                null,
                8m,
                "Feedback",
                [new AiGradingCriterionProposal("C1", "Criterio", 10m, 8m, AiGradingCriterionSource.FormalRubric, "evidencia", null, false, false, [])],
                [],
                [],
                new GradingScaleSnapshot(10m, "points", "Moodle"),
                new GradingExtractionSummary("succeeded", 1, false, 10, 10, null),
                new GradingEvidenceCoverage(1, 1, 1, 1, 10, 10, false),
                new AiGradingConfidenceResult(.9m, [], false),
                reviewRequired: true);
            await store.PublishAsync(proposal, CancellationToken.None);
            await store.PublishAsync(proposal, CancellationToken.None);
            await store.SaveChangesAsync(CancellationToken.None);
        }

        await using (var verifyDb = new ConnectorDbContext(options))
        {
            var documents = await verifyDb.AiGradingProposals
                .Where(proposal => proposal.GradingItemId == item.Id)
                .ToArrayAsync();
            var document = Assert.Single(documents);
            Assert.Equal(AiGradingProposal.CurrentSchemaVersion, document.SchemaVersion);
            Assert.Matches("^[0-9a-f]{64}$", document.ProposalHash);
            Assert.Contains("C1", document.PayloadJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DeferredArtifactSource_PersistsAndCanBeMaterializedWithoutToken()
    {
        var connectionString = GetConnectionStringOrFail();

        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(10, [501], $"integration-artifact-{Guid.NewGuid():N}", 321, 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var artifact = new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "entrega.txt",
            "text/plain",
            Sha256: null,
            SizeBytes: 31,
            ExtractionStatus.Pending,
            ExtractedTextRef: null,
            SummaryRef: "pending_ingestion",
            DateTimeOffset.UtcNow,
            "https://moodle.example/pluginfile.php/entrega.txt");

        await using (var seedDb = new ConnectorDbContext(options))
        {
            seedDb.GradingBatches.Add(batch);
            seedDb.GradingItems.Add(item);
            seedDb.GradingArtifacts.Add(artifact);
            await seedDb.SaveChangesAsync();
        }

        await using (var updateDb = new ConnectorDbContext(options))
        {
            var store = new GradingReviewRepository(updateDb);
            var persisted = Assert.Single(await store.ListArtifactsByItemAsync(item.Id, CancellationToken.None));
            Assert.Equal("https://moodle.example/pluginfile.php/entrega.txt", persisted.SourceUrl);

            await store.UpdateArtifactAsync(
                persisted with
                {
                    ExtractionStatus = ExtractionStatus.Succeeded,
                    ExtractedTextRef = "texto extraido",
                    SummaryRef = null,
                    SourceUrl = null
                },
                CancellationToken.None);
            await store.SaveChangesAsync(CancellationToken.None);
        }

        await using (var verifyDb = new ConnectorDbContext(options))
        {
            var persisted = Assert.Single(await verifyDb.GradingArtifacts
                .Where(candidate => candidate.Id == artifact.Id)
                .ToArrayAsync());
            Assert.Equal(ExtractionStatus.Succeeded, persisted.ExtractionStatus);
            Assert.Equal("texto extraido", persisted.ExtractedTextRef);
            Assert.Null(persisted.SourceUrl);
        }
    }

    private static string GetConnectionStringOrFail()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");
        return connectionString!;
    }
}
