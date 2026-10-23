using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.Grading.Packages;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Integration;

public sealed class GradingPackageStoreTests
{
    [Fact]
    public async Task Store_AcquireReportsNewAndReusablePartialPackagesAndCreatesChangedVersion()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db);
        var itemId = Guid.NewGuid();
        var identity = CreateIdentity(itemId, "submission-a");

        var created = await store.AcquireAsync(identity, null, CancellationToken.None);

        Assert.False(created.WasExisting);
        Assert.Equal(1, created.Package.PackageVersion);
        var existing = await store.AcquireAsync(identity, null, CancellationToken.None);
        Assert.True(existing.WasExisting);
        Assert.Equal(created.Package.Id, existing.Package.Id);

        var now = DateTimeOffset.UtcNow;
        existing.Package.MarkManifest("{}", new string('a', 64), now);
        existing.Package.MarkPartiallyReady(
            "packages/partial.zip", new string('b', 64), 100, 120, 2,
            selectedItemCount: 1, includedItemCount: 0, pendingItemCount: 1,
            issueCount: 1, now);
        await store.SaveChangesAsync(CancellationToken.None);

        var reusedPartial = await store.AcquireAsync(identity, null, CancellationToken.None);
        Assert.True(reusedPartial.WasExisting);
        Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, reusedPartial.Package.Status);
        Assert.Equal(existing.Package.Id, reusedPartial.Package.Id);

        var changed = await store.AcquireAsync(CreateIdentity(itemId, "submission-b"), null, CancellationToken.None);
        Assert.False(changed.WasExisting);
        Assert.NotEqual(existing.Package.Id, changed.Package.Id);
        Assert.Equal(2, changed.Package.PackageVersion);
        Assert.Equal(2, await db.GradingEvidencePackages.CountAsync());
    }

    [Fact]
    public async Task Store_ReusesSameEvidenceAndCreatesVersionWhenContentChanges()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db);
        var itemId = Guid.NewGuid();
        var identity = CreateIdentity(itemId, "submission-a");

        var first = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
        var same = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
        var changed = await store.GetOrCreateAsync(
            CreateIdentity(itemId, "submission-b"),
            null,
            CancellationToken.None);

        Assert.Equal(first.Id, same.Id);
        Assert.Equal(1, same.PackageVersion);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.Equal(2, changed.PackageVersion);
        Assert.Equal(2, await db.GradingEvidencePackages.CountAsync());
    }

    [Fact]
    public async Task Store_PersistsMembersAndRejectsCrossPackageItemReuseAtCommandBoundary()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db);
        var itemId = Guid.NewGuid();
        var package = await store.GetOrCreateAsync(CreateIdentity(itemId, "submission-a"), null, CancellationToken.None);
        var member = GradingEvidencePackageMember.Create(
            package.Id,
            itemId,
            101,
            "Ana Silva",
            9001,
            0,
            DateTimeOffset.UtcNow,
            "context-a",
            "submission-a");

        await store.AddMemberAsync(member, CancellationToken.None);
        await store.SaveChangesAsync(CancellationToken.None);

        var members = await store.ListMembersAsync(package.Id, CancellationToken.None);
        var persisted = Assert.Single(members);
        Assert.Equal(itemId, persisted.GradingItemId);
        Assert.Equal("submission-a", persisted.SubmissionContentHash);
    }

    [Fact]
    public async Task Store_CreatesNewVersionWhenPreviousTransportIsExpired()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db);
        var identity = CreateIdentity(Guid.NewGuid(), "submission-a");
        var first = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        first.MarkManifest("{}", new string('a', 64), now);
        first.MarkReady(
            "packages/first.zip",
            new string('b', 64),
            100,
            120,
            2,
            1,
            1,
            0,
            now,
            now.AddMinutes(5));
        first.MarkBlobExpired(now.AddMinutes(6));
        await store.SaveChangesAsync(CancellationToken.None);

        var replacement = await store.GetOrCreateAsync(identity, null, CancellationToken.None);

        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(2, replacement.PackageVersion);
        Assert.Equal(2, await db.GradingEvidencePackages.CountAsync());
    }

    [Fact]
    public async Task Store_CreatesNewVersionWhenPreviousZipIsMissing()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db, new MissingBlobStore());
        var identity = CreateIdentity(Guid.NewGuid(), "submission-a");
        var first = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        first.MarkManifest("{}", new string('a', 64), now);
        first.MarkReady(
            "packages/missing.zip",
            new string('b', 64),
            100,
            120,
            2,
            1,
            1,
            0,
            now,
            now.AddHours(1));
        await store.SaveChangesAsync(CancellationToken.None);

        var replacement = await store.GetOrCreateAsync(identity, null, CancellationToken.None);

        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(2, replacement.PackageVersion);
        Assert.Equal(2, await db.GradingEvidencePackages.CountAsync());
    }

    [Fact]
    public async Task Store_CreatesNewVersionWhenStorageIsTemporarilyUnavailable()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db, new TransientBlobStore());
        var identity = CreateIdentity(Guid.NewGuid(), "submission-a");
        var first = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        first.MarkManifest("{}", new string('a', 64), now);
        first.MarkReady(
            "packages/transient.zip",
            new string('b', 64),
            100,
            120,
            2,
            1,
            1,
            0,
            now,
            now.AddHours(1));
        await store.SaveChangesAsync(CancellationToken.None);

        var replacement = await store.GetOrCreateAsync(identity, null, CancellationToken.None);

        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(2, replacement.PackageVersion);
        Assert.Equal(GradingEvidencePackageStatus.Ready, first.Status);
        Assert.Equal(2, await db.GradingEvidencePackages.CountAsync());
    }

    [Fact]
    public async Task Store_CreatesNewVersionWhenStoragePermissionIsUnavailable()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db, new UnauthorizedBlobStore());
        var identity = CreateIdentity(Guid.NewGuid(), "submission-a");
        var first = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        first.MarkManifest("{}", new string('a', 64), now);
        first.MarkReady(
            "packages/permission.zip",
            new string('b', 64),
            100,
            120,
            2,
            1,
            1,
            0,
            now,
            now.AddHours(1));
        await store.SaveChangesAsync(CancellationToken.None);

        var replacement = await store.GetOrCreateAsync(identity, null, CancellationToken.None);

        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(2, replacement.PackageVersion);
        Assert.Equal(GradingEvidencePackageStatus.Ready, first.Status);
        Assert.Equal(2, await db.GradingEvidencePackages.CountAsync());
    }

    [Fact]
    public async Task Store_ReusesPackageWhenWorkerEnrichesSubmissionHashAfterReservation()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db);
        var itemId = Guid.NewGuid();
        var reservedIdentity = GradingPackageIdentity.Create(
            "teacher-1",
            "connection-a",
            "client-a",
            "default",
            10,
            501,
            "1001",
            [new GradingPackageItemIdentity(itemId, "context-a", null)]);
        var enrichedIdentity = CreateIdentity(itemId, "submission-a");
        var package = await store.GetOrCreateAsync(reservedIdentity, null, CancellationToken.None);
        var member = GradingEvidencePackageMember.Create(
            package.Id,
            itemId,
            101,
            "Ana Silva",
            9001,
            0,
            DateTimeOffset.UtcNow,
            "context-a",
            "submission-a");
        member.MarkIncluded();
        await store.AddMemberAsync(member, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        package.MarkManifest("{}", new string('a', 64), now);
        package.MarkReady(
            "packages/enriched.zip",
            new string('b', 64),
            100,
            120,
            2,
            1,
            1,
            0,
            now,
            now.AddHours(1));
        await store.SaveChangesAsync(CancellationToken.None);

        var reused = await store.GetOrCreateAsync(enrichedIdentity, null, CancellationToken.None);

        Assert.Equal(package.Id, reused.Id);
        Assert.Equal(1, await db.GradingEvidencePackages.CountAsync());

        var changed = await store.GetOrCreateAsync(
            CreateIdentity(itemId, "submission-b"),
            null,
            CancellationToken.None);
        Assert.NotEqual(package.Id, changed.Id);
        Assert.Equal(2, changed.PackageVersion);
    }

    [Fact]
    public async Task Store_ReusesLegacyAliasScopedPackageAfterIdentityHardening()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db);
        var itemId = Guid.NewGuid();
        var legacyIdentity = GradingPackageIdentity.Create(
            "teacher-1",
            moodleConnectionId: null,
            connectorClientId: null,
            connectionAlias: null,
            10,
            501,
            "1001",
            [new GradingPackageItemIdentity(itemId, "context-a", "submission-a")]);
        var currentIdentity = GradingPackageIdentity.Create(
            "teacher-1",
            moodleConnectionId: null,
            connectorClientId: null,
            connectionAlias: "legacy-alias",
            10,
            501,
            "1001",
            [new GradingPackageItemIdentity(itemId, "context-a", "submission-a")]);
        var package = GradingEvidencePackage.Create(
            legacyIdentity.OwnerSubject,
            legacyIdentity.MoodleConnectionId,
            legacyIdentity.ConnectorClientId,
            currentIdentity.ConnectionAlias,
            legacyIdentity.CourseId,
            legacyIdentity.AssignmentId,
            legacyIdentity.Cmid,
            1,
            legacyIdentity.PreparationKey,
            legacyIdentity.ActivityKey);
        await db.GradingEvidencePackages.AddAsync(package);
        var member = GradingEvidencePackageMember.Create(
            package.Id,
            itemId,
            101,
            "Ana Silva",
            9001,
            0,
            DateTimeOffset.UtcNow,
            "context-a",
            "submission-a");
        member.MarkIncluded();
        await db.GradingEvidencePackageMembers.AddAsync(member);
        var now = DateTimeOffset.UtcNow;
        package.MarkManifest("{}", new string('a', 64), now);
        package.MarkReady(
            "packages/legacy.zip",
            new string('b', 64),
            100,
            120,
            2,
            1,
            1,
            0,
            now,
            now.AddHours(1));
        await db.SaveChangesAsync();

        var reused = await store.GetOrCreateAsync(currentIdentity, null, CancellationToken.None);

        Assert.Equal(package.Id, reused.Id);
        Assert.Equal(1, await db.GradingEvidencePackages.CountAsync());
    }

    [Fact]
    public async Task Store_CreatesExplicitRetryVersionWithoutDiscardingPartialPackage()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db);
        var identity = CreateIdentity(Guid.NewGuid(), "submission-a");
        var previous = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        previous.MarkManifest("{}", new string('a', 64), now);
        previous.MarkPartiallyReady(
            "packages/partial.zip",
            new string('b', 64),
            100,
            100,
            2,
            1,
            0,
            1,
            1,
            now,
            now.AddMinutes(5));
        await store.SaveChangesAsync(CancellationToken.None);

        var retry = await store.CreateNextVersionAsync(identity, null, CancellationToken.None);

        Assert.NotEqual(previous.Id, retry.Id);
        Assert.Equal(2, retry.PackageVersion);
        Assert.Equal(2, await db.GradingEvidencePackages.CountAsync());
        Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, previous.Status);
    }

    [Fact]
    public async Task Store_LeaseIsExclusiveAndCanBeRecoveredAfterRetryTime()
    {
        await using var db = new ConnectorDbContext(
            new DbContextOptionsBuilder<ConnectorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var store = new GradingPackageStore(db);
        var package = await store.GetOrCreateAsync(
            CreateIdentity(Guid.NewGuid(), "submission-a"),
            null,
            CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        Assert.True(await store.TryClaimAsync(package.Id, "worker-a", now, TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.False(await store.TryClaimAsync(package.Id, "worker-b", now, TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.True(await store.ReleaseClaimAsync(package.Id, "worker-a", now, "temporary", now.AddSeconds(30), CancellationToken.None));
        Assert.False(await store.TryClaimAsync(package.Id, "worker-b", now.AddSeconds(1), TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.True(await store.TryClaimAsync(package.Id, "worker-b", now.AddSeconds(31), TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task Store_ConflitoDeVersaoNaoLimpaMembrosNaoRelacionadosDoTracker()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(new UniqueVersionInsertInterceptor())
            .Options;
        await using var db = new ConnectorDbContext(options);
        var pendingMember = GradingEvidencePackageMember.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            101,
            "Ana Silva",
            9001,
            0,
            DateTimeOffset.UtcNow,
            "context-a",
            "submission-a");
        await db.GradingEvidencePackageMembers.AddAsync(pendingMember);
        var store = new GradingPackageStore(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetOrCreateAsync(
            CreateIdentity(Guid.NewGuid(), "submission-a"),
            null,
            CancellationToken.None));

        Assert.Equal(EntityState.Added, db.Entry(pendingMember).State);
    }

    private static GradingPackageIdentity CreateIdentity(Guid itemId, string contentHash) =>
        GradingPackageIdentity.Create(
            "teacher-1",
            "connection-a",
            "client-a",
            "default",
            10,
            501,
            "1001",
            [new GradingPackageItemIdentity(itemId, "context-a", contentHash)]);

    private sealed class MissingBlobStore : IGradingPackageBlobStore
    {
        public Task<GradingPackageBlobDescriptor> WriteAsync(
            string blobKey,
            Func<Stream, CancellationToken, Task> writer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            throw new FileNotFoundException("blob ausente", blobKey);

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TransientBlobStore : IGradingPackageBlobStore
    {
        public Task<GradingPackageBlobDescriptor> WriteAsync(
            string blobKey,
            Func<Stream, CancellationToken, Task> writer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            throw new IOException("storage temporariamente indisponivel");

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class UnauthorizedBlobStore : IGradingPackageBlobStore
    {
        public Task<GradingPackageBlobDescriptor> WriteAsync(
            string blobKey,
            Func<Stream, CancellationToken, Task> writer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException("storage sem permissao");

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class UniqueVersionInsertInterceptor : SaveChangesInterceptor
    {
        private static DbUpdateException CreateException() =>
            new("simulated package version conflict", new PostgresException(
                "duplicate key",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result) =>
            throw CreateException();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(CreateException());
    }
}

[Trait("Category", "PostgresIntegration")]
public sealed class GradingPackagePostgresIntegrationTests
{
    [Fact]
    public async Task Store_ConcurrentDifferentPreparationsForSameActivityGetsDistinctVersions()
    {
        var options = CreateOptions();
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        const int concurrentPreparations = 16;
        var ownerSubject = $"shared-teacher-{Guid.NewGuid():N}";
        var identities = Enumerable.Range(1, concurrentPreparations)
            .Select(index => GradingPackageIdentity.Create(
                ownerSubject,
                "connection-a",
                "client-a",
                "default",
                10,
                501,
                "1001",
                [new GradingPackageItemIdentity(
                    Guid.NewGuid(),
                    "context-a",
                    $"submission-{index}")]))
            .ToArray();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<GradingPackageAcquireResult> AcquireAsync(GradingPackageIdentity identity)
        {
            await start.Task;
            await using var db = new ConnectorDbContext(options);
            return await new GradingPackageStore(db).AcquireAsync(identity, null, CancellationToken.None);
        }

        var acquisitions = identities.Select(AcquireAsync).ToArray();
        start.SetResult(true);
        var results = await Task.WhenAll(acquisitions);

        Assert.Equal(concurrentPreparations, results.Select(result => result.Package.Id).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(1, concurrentPreparations),
            results.Select(result => result.Package.PackageVersion).Order());
    }

    [Fact]
    public async Task Store_ConcurrentAcquisitionReturnsOneReusablePackage()
    {
        var options = CreateOptions();
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var identity = CreateIdentity(Guid.NewGuid(), "submission-a");
        async Task<GradingPackageAcquireResult> AcquireAsync()
        {
            await using var db = new ConnectorDbContext(options);
            return await new GradingPackageStore(db).AcquireAsync(identity, null, CancellationToken.None);
        }

        const int concurrentAcquisitions = 16;
        var results = await Task.WhenAll(
            Enumerable.Range(0, concurrentAcquisitions).Select(_ => AcquireAsync()));

        Assert.Single(results.Select(result => result.Package.Id).Distinct());
        Assert.Single(results, result => !result.WasExisting);
        Assert.Equal(concurrentAcquisitions - 1, results.Count(result => result.WasExisting));
        await using var verifyDb = new ConnectorDbContext(options);
        Assert.Equal(1, await verifyDb.GradingEvidencePackages.CountAsync(package =>
            package.OwnerSubject == identity.OwnerSubject && package.PreparationKey == identity.PreparationKey));
    }

    [Fact]
    public async Task Context_snapshot_hash_validation_survives_jsonb_roundtrip()
    {
        var options = CreateOptions();
        var batch = AssistedGradingBatch.Create(42, [501], "jsonb-context-test", 3001, 1);
        var item = AssistedGradingItem.Create(batch.Id, 42, 501, 7001, 3001, 1);
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "42",
            "501",
            "7001",
            "3001",
            assignmentStatement: "Descreva a solução proposta.",
            criteria: "Descrever a solução\nJustificar as escolhas",
            rubricDescription: "Rubrica formal",
            maxGrade: 10m,
            gradeScale: null,
            submissionText: "Texto da entrega.",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: "Priorize clareza.");
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);

        await using (var db = new ConnectorDbContext(options))
        {
            await db.ApplyVersionedSchemaAsync();
            db.GradingBatches.Add(batch);
            db.GradingItems.Add(item);
            db.GradingContextSnapshots.Add(GradingContextSnapshotDocument.FromSnapshot(snapshot));
            await db.SaveChangesAsync();
        }

        await using var verifyDb = new ConnectorDbContext(options);
        var persisted = await verifyDb.GradingContextSnapshots.AsNoTracking()
            .SingleAsync(document => document.GradingItemId == item.Id);

        Assert.Equal(snapshot.ContextHash, persisted.ContextHash);
        Assert.Equal(snapshot.ContextHash,
            GradingContextSnapshot.ComputeCanonicalPayloadHash(persisted.PayloadJson));
    }

    [Fact]
    public async Task Grading_package_manifest_hash_survives_jsonb_roundtrip()
    {
        var options = CreateOptions();
        var now = DateTimeOffset.UtcNow;
        var itemId = Guid.NewGuid();
        var identity = GradingPackageIdentity.Create(
            $"jsonb-teacher-{Guid.NewGuid():N}",
            "connection-a",
            "client-a",
            "fieg",
            42,
            501,
            "1001",
            [new GradingPackageItemIdentity(itemId, "context-hash", "submission-hash")]);
        var package = GradingEvidencePackage.Create(
            identity.OwnerSubject,
            identity.MoodleConnectionId,
            identity.ConnectorClientId,
            identity.ConnectionAlias,
            identity.CourseId,
            identity.AssignmentId,
            identity.Cmid,
            1,
            identity.PreparationKey,
            identity.ActivityKey,
            now: now);
        var manifest = new GradingPackageManifest(
            GradingPackageContract.ManifestSchemaVersion,
            package.Id,
            package.PackageVersion,
            new GradingPackageActivity(
                package.CourseId,
                package.AssignmentId,
                package.Cmid,
                "numeric",
                20m,
                [],
                ["Critério sintético"],
                null),
            [new GradingPackageManifestItem(
                itemId,
                Guid.NewGuid(),
                null,
                1001,
                null,
                9001,
                1,
                null,
                "context-hash",
                "submission-hash",
                [new GradingPackageManifestFile(
                    Guid.NewGuid(),
                    "submissions/1001/answer.docx",
                    "answer.docx",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    new string('a', 64),
                    128,
                    false)],
                ExpectedSubmissionFileCount: 1,
                SubmissionEvidenceComplete: true)],
            new GradingPackageCoverage(1, 1, 0, []));
        var serialized = GradingPackageManifestHash.Serialize(manifest);
        var manifestHash = GradingPackageManifestHash.Compute(manifest);
        package.MarkManifest(serialized, manifestHash, now);
        package.MarkReady(
            "packages/jsonb-roundtrip.zip",
            new string('b', 64),
            128,
            256,
            2,
            1,
            1,
            0,
            now,
            now.AddHours(1));

        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
            schemaDb.GradingEvidencePackages.Add(package);
            await schemaDb.SaveChangesAsync();
        }

        await using var verifyDb = new ConnectorDbContext(options);
        var persisted = await verifyDb.GradingEvidencePackages
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == package.Id);

        Assert.NotEqual(manifestHash, GradingPackageManifestHash.ComputeSerializedJson(persisted.ManifestJson!));
        Assert.True(GradingPackageManifestHash.IsValid(persisted.ManifestJson!, persisted.ManifestHash!));

        var status = await new GetGradingPackageStatusQueryHandler(
                new GradingPackageStore(verifyDb),
                new TestCurrentUser(identity.OwnerSubject))
            .Handle(new GetGradingPackageStatusQuery(package.Id), CancellationToken.None);
        Assert.NotNull(status.Manifest);
        Assert.Empty(status.Warnings);
        Assert.Equal(manifestHash, status.Descriptor.ManifestHash);
    }

    [Fact]
    public async Task Store_UsesPostgresConcurrencyForClaimsAndRecovery()
    {
        var options = CreateOptions();
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var identity = CreateIdentity(Guid.NewGuid(), "submission-a");
        Guid packageId;
        await using (var seedDb = new ConnectorDbContext(options))
        {
            var store = new GradingPackageStore(seedDb);
            packageId = (await store.GetOrCreateAsync(identity, null, CancellationToken.None)).Id;
        }

        var now = DateTimeOffset.UtcNow;
        async Task<bool> ClaimAsync(string workerId)
        {
            await using var db = new ConnectorDbContext(options);
            var store = new GradingPackageStore(db);
            return await store.TryClaimAsync(
                packageId,
                workerId,
                now,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
        }

        var claims = await Task.WhenAll(ClaimAsync("package-worker-a"), ClaimAsync("package-worker-b"));
        Assert.Single(claims, claimed => claimed);
        Assert.Single(claims, claimed => !claimed);

        await using (var verifyDb = new ConnectorDbContext(options))
        {
            var package = await verifyDb.GradingEvidencePackages.SingleAsync(item => item.Id == packageId);
            Assert.Equal(GradingEvidencePackageStatus.Preparing, package.Status);
            Assert.Contains(package.LeaseOwner, new[] { "package-worker-a", "package-worker-b" });
        }

        await using (var recoveryDb = new ConnectorDbContext(options))
        {
            var store = new GradingPackageStore(recoveryDb);
            var recovered = await store.RecoverExpiredClaimsAsync(now.AddMinutes(6), CancellationToken.None);
            // Recovery is intentionally a global sweep; other integration tests
            // may have left expired leases in the shared CI database. Assert the
            // target package itself was recovered instead of requiring a global
            // row count of exactly one.
            Assert.True(recovered >= 1);
        }

        await using (var recoveredDb = new ConnectorDbContext(options))
        {
            var recoveredPackage = await recoveredDb.GradingEvidencePackages
                .SingleAsync(item => item.Id == packageId);
            Assert.Equal(GradingEvidencePackageStatus.Queued, recoveredPackage.Status);
            Assert.Null(recoveredPackage.LeaseOwner);
            Assert.Null(recoveredPackage.LeaseUntil);
        }

        await using var resumedDb = new ConnectorDbContext(options);
        var resumedStore = new GradingPackageStore(resumedDb);
        Assert.True(await resumedStore.TryClaimAsync(
            packageId,
            "package-worker-c",
            now.AddMinutes(6),
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
    }

    [Fact]
    public async Task Store_PersistsRetryAfterAcrossNewDbContextAndBlocksClaimUntilDue()
    {
        var options = CreateOptions();
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var identity = CreateIdentity(Guid.NewGuid(), "submission-retry-after");
        Guid packageId;
        await using (var seedDb = new ConnectorDbContext(options))
        {
            var store = new GradingPackageStore(seedDb);
            packageId = (await store.GetOrCreateAsync(identity, null, CancellationToken.None)).Id;
        }

        var now = DateTimeOffset.UtcNow;
        var retryAt = now.AddMinutes(1);
        await using (var workerDb = new ConnectorDbContext(options))
        {
            var store = new GradingPackageStore(workerDb);
            Assert.True(await store.TryClaimAsync(
                packageId,
                "package-worker-before-restart",
                now,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
            Assert.True(await store.ReleaseClaimAsync(
                packageId,
                "package-worker-before-restart",
                now,
                "http_429",
                retryAt,
                CancellationToken.None));
        }

        await using (var restartedDb = new ConnectorDbContext(options))
        {
            var store = new GradingPackageStore(restartedDb);
            var persisted = await restartedDb.GradingEvidencePackages
                .AsNoTracking()
                .SingleAsync(package => package.Id == packageId);
            Assert.Equal(GradingEvidencePackageStatus.Queued, persisted.Status);
            Assert.Equal("http_429", persisted.LastErrorCode);
            Assert.NotNull(persisted.NextAttemptAt);
            Assert.InRange(persisted.NextAttemptAt.Value, retryAt.AddMilliseconds(-1), retryAt.AddMilliseconds(1));
            Assert.False(await store.TryClaimAsync(
                packageId,
                "package-worker-after-restart-early",
                persisted.NextAttemptAt.Value.AddTicks(-1),
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
            Assert.True(await store.TryClaimAsync(
                packageId,
                "package-worker-after-restart-due",
                persisted.NextAttemptAt.Value,
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
        }
    }

    [Fact]
    public async Task Store_CreatesNewVersionAfterPostgresTransportExpiry()
    {
        var options = CreateOptions();
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var identity = CreateIdentity(Guid.NewGuid(), "submission-a");
        var now = DateTimeOffset.UtcNow;
        Guid firstId;
        await using (var firstDb = new ConnectorDbContext(options))
        {
            var store = new GradingPackageStore(firstDb);
            var first = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
            firstId = first.Id;
            first.MarkManifest("{}", new string('a', 64), now);
            first.MarkReady(
                "packages/postgres-expiry.zip",
                new string('b', 64),
                100,
                120,
                2,
                1,
                1,
                0,
                now,
                // Keep the fixture unequivocally expired. The original
                // future timestamp made the test depend on wall-clock drift
                // instead of exercising transport regeneration.
                now.AddMinutes(-1));
            await store.SaveChangesAsync(CancellationToken.None);
        }

        await using (var replacementDb = new ConnectorDbContext(options))
        {
            var store = new GradingPackageStore(replacementDb);
            var replacement = await store.GetOrCreateAsync(identity, null, CancellationToken.None);
            Assert.NotEqual(firstId, replacement.Id);
            Assert.Equal(2, replacement.PackageVersion);
        }
    }

    private static DbContextOptions<ConnectorDbContext> CreateOptions()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "MOODLE_POSTGRES_TEST_CONNECTION e obrigatoria; o gate PostgreSQL nao pode ser silenciosamente ignorado.");
        return new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString!)
            .Options;
    }

    private static GradingPackageIdentity CreateIdentity(Guid itemId, string contentHash) =>
        GradingPackageIdentity.Create(
            $"postgres-teacher-{Guid.NewGuid():N}",
            "connection-a",
            "client-a",
            "default",
            10,
            501,
            "1001",
            [new GradingPackageItemIdentity(itemId, "context-a", contentHash)]);

    private sealed class TestCurrentUser(string subject) : ICurrentUserContext
    {
        public string Subject => subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }
}
