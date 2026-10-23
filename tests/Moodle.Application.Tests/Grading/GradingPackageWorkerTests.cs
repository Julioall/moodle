using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Application.Grading.Packages;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;
using Xunit.Abstractions;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPackageWorkerTests
{
    private readonly ITestOutputHelper output;

    public GradingPackageWorkerTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Synthetic_package_load_matrix_tracks_packages_files_calls_bytes_and_duration()
    {
        const int fileSizeBytes = 128;
        var payload = Enumerable.Repeat((byte)0x5a, fileSizeBytes).ToArray();
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        foreach (var packageCount in new[] { 1, 20, 100 })
        {
            foreach (var filesPerItem in new[] { 0, 1, 10 })
            {
                var repository = new FakeReviewRepository();
                var batch = AssistedGradingBatch.Create(
                    10, [501], "synthetic-owner", 321, packageCount,
                    connectorClientId: "synthetic-client",
                    connectionAlias: "synthetic-alias",
                    moodleConnectionId: "synthetic-connection");
                repository.Batches.Add(batch);
                var items = Enumerable.Range(0, packageCount)
                    .Select(index =>
                    {
                        var item = AssistedGradingItem.Create(
                            batch.Id, 10, 501, 9000 + index, 10000 + index, 1, $"synthetic-student-{index}");
                        item.MarkAwaitingAiAnalysis(null);
                        return item;
                    })
                    .ToArray();
                repository.Items.AddRange(items);
                foreach (var (item, itemIndex) in items.Select((item, itemIndex) => (item, itemIndex)))
                {
                    for (var fileIndex = 0; fileIndex < filesPerItem; fileIndex++)
                    {
                        repository.Artifacts.Add(new GradingArtifact(
                            Guid.NewGuid(),
                            item.Id,
                            "submission_file",
                            $"synthetic-{itemIndex}-{fileIndex}.txt",
                            "text/plain",
                            payloadHash,
                            payload.LongLength,
                            ExtractionStatus.Succeeded,
                            null,
                            null,
                            DateTimeOffset.UtcNow,
                            $"https://moodle.example/pluginfile.php/synthetic/{itemIndex}/{fileIndex}"));
                    }
                }

                var records = items.Select(item => new AssignmentSubmissionRecord(
                    item.SubmissionId!.Value.ToString(),
                    item.MoodleUserId.ToString(),
                    "submitted",
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    1,
                    1,
                    false,
                    Files: [],
                    OnlineText: null)).ToArray();
                var identity = GradingPackageIdentity.Create(
                    batch.CreatedBySubject,
                    batch.MoodleConnectionId,
                    batch.ConnectorClientId,
                    batch.ConnectionAlias,
                    batch.CourseId,
                    501,
                    null,
                    [new GradingPackageItemIdentity(items[0].Id, items[0].ContextHash, items[0].SubmissionContentHash)]);
                var firstPackage = GradingEvidencePackage.Create(
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
                    batch.Id);
                var packageStore = new FakePackageStore(firstPackage) { CreatePackagePerIdentity = true };
                var fileGateway = new SyntheticFileDownloadGateway(payload, payloadHash);
                var submissionsGateway = new SyntheticAssignmentSubmissionsGateway(records);
                var worker = CreateWorker(
                    repository,
                    packageStore,
                    submissionsGateway: submissionsGateway,
                    limits: new GradingLimitsOptions
                    {
                        BatchLeaseMinutes = 1,
                        MaxGradingPackageItems = 1,
                        MaxGradingPackageEntries = 16,
                        MaxGradingPackageZipMb = 4,
                        MaxGradingPackageUncompressedMb = 4,
                        MaxFileSizeMb = 1,
                        MaxResourceBytes = 1024 * 1024
                    },
                    submissionFileGateway: fileGateway);

                var stopwatch = Stopwatch.StartNew();
                await worker.PrepareAsync(batch.Id, null, CancellationToken.None);
                stopwatch.Stop();

                Assert.Equal(packageCount, packageStore.Packages.Count);
                Assert.Equal(packageCount * filesPerItem, fileGateway.DownloadCount);
                Assert.All(packageStore.Packages, package => Assert.NotNull(package.ManifestJson));
                var downloadedBytes = (long)fileGateway.DownloadCount * fileSizeBytes;
                output.WriteLine(
                    $"synthetic packages={packageCount} items={packageCount} files_per_item={filesPerItem} " +
                    $"downloads={fileGateway.DownloadCount} bytes={downloadedBytes} " +
                    $"submission_batch_calls={submissionsGateway.BatchReadCalls} elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F2} " +
                    "submission_read_cache=off grade_publication=none");
            }
        }
    }

    [Fact]
    public async Task Context_file_bytes_are_verified_against_snapshot_hash_before_package_inclusion()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1,
            connectorClientId: "client-a", connectionAlias: "alias-a", moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.Add(item);

        var submissionContent = Encoding.UTF8.GetBytes("comprovacao enviada pelo aluno");
        var submissionHash = Convert.ToHexString(SHA256.HashData(submissionContent)).ToLowerInvariant();
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(), item.Id, "submission_file", "resposta.txt", "text/plain",
            submissionHash, submissionContent.LongLength, ExtractionStatus.Succeeded,
            Encoding.UTF8.GetString(submissionContent), null, DateTimeOffset.UtcNow));

        var expectedContextContent = Encoding.UTF8.GetBytes("enunciado esperado");
        var expectedContextHash = Convert.ToHexString(SHA256.HashData(expectedContextContent)).ToLowerInvariant();
        var returnedContextContent = Encoding.UTF8.GetBytes("bytes diferentes do snapshot");
        var returnedContextHash = Convert.ToHexString(SHA256.HashData(returnedContextContent)).ToLowerInvariant();
        var contextArtifactId = Guid.NewGuid();
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            "9001",
            "101",
            assignmentStatement: "Resolva a atividade.",
            criteria: "Correção e justificativa",
            rubricDescription: null,
            maxGrade: 10m,
            gradeScale: null,
            submissionText: "resposta",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: null,
            artifactReferences:
            [
                new GradingArtifactReferenceSnapshot(
                    contextArtifactId,
                    "assignment_context",
                    "enunciado.pdf",
                    "application/pdf",
                    expectedContextHash,
                    expectedContextContent.LongLength,
                    ExtractionStatus.Succeeded,
                    1,
                    false,
                    expectedContextContent.Length,
                    expectedContextContent.Length,
                    Source: null)
            ]);
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        repository.ContextSnapshots[item.Id] = GradingContextSnapshotDocument.FromSnapshot(snapshot);
        repository.Artifacts.Add(new GradingArtifact(
            contextArtifactId, item.Id, "assignment_context", "enunciado.pdf", "application/pdf",
            expectedContextHash, expectedContextContent.LongLength, ExtractionStatus.Succeeded,
            null, null, DateTimeOffset.UtcNow, "https://moodle.example/context.pdf"));

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
        var package = GradingEvidencePackage.Create(
            identity.OwnerSubject, identity.MoodleConnectionId, identity.ConnectorClientId,
            identity.ConnectionAlias, identity.CourseId, identity.AssignmentId, identity.Cmid,
            1, identity.PreparationKey, identity.ActivityKey, batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, item));
        var worker = CreateWorker(
            repository,
            packageStore,
            submissionFileGateway: new ConcurrentSubmissionFileGateway(returnedContextContent, returnedContextHash));

        await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, package.Status);
        var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(package.ManifestJson!, GradingPackageContract.JsonOptions);
        var manifestItem = Assert.Single(manifest!.Items);
        Assert.False(manifestItem.ContextAvailable);
        Assert.DoesNotContain(manifestItem.Files, file => file.IsContext);
        Assert.Contains(manifest.Coverage.Issues, issue => issue.Code == "artifact_read_failed");
    }

    [Fact]
    public async Task Context_cache_reuses_verified_content_across_parts_with_distinct_artifact_ids()
    {
        var repository = new FakeReviewRepository();
        var runId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("Enunciado compartilhado");
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var url = "https://moodle.example/context.pdf";
        var packages = new List<GradingEvidencePackage>();
        foreach (var index in Enumerable.Range(0, 2))
        {
            var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: runId);
            var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001 + index, 101 + index, 1, "Aluno");
            item.MarkAwaitingAiAnalysis(null);
            repository.Batches.Add(batch);
            repository.Items.Add(item);
            repository.Artifacts.Add(new GradingArtifact(Guid.NewGuid(), item.Id, "assignment_context",
                "enunciado.pdf", "application/pdf", sha, content.Length, ExtractionStatus.Succeeded,
                null, null, DateTimeOffset.UtcNow, url));
            var identity = GradingPackageIdentity.Create("teacher-1", null, null, null, 10, 501, null,
                [new GradingPackageItemIdentity(item.Id, null, null)]);
            packages.Add(GradingEvidencePackage.Create("teacher-1", null, null, null, 10, 501, null, 1,
                identity.PreparationKey, identity.ActivityKey, batch.Id));
        }
        var store = new FakePackageStore(packages[0]);
        store.Packages.Add(packages[1]);
        for (var index = 0; index < 2; index++) store.Members.Add(CreateMember(packages[index], repository.Items[index]));
        var cache = new InMemoryArtifactCache();
        var gateway = new RetryAwareSubmissionFileGateway(new Dictionary<string, byte[]> { [url] = content }, "unused");
        foreach (var package in packages)
        {
            var worker = CreateWorker(repository, store, submissionFileGateway: gateway, artifactCache: cache);
            await worker.PrepareAsync(package.BatchJobId!.Value, package.Id, default);
        }

        Assert.Equal(1, gateway.Calls[url]);
        Assert.Equal(1, cache.HitCount);
        Assert.All(packages, package => Assert.NotNull(package.ManifestJson));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("mixed_context")]
    [InlineData("real_context")]
    [InlineData("other_run")]
    [InlineData("no_run")]
    [InlineData("owner")]
    [InlineData("connection")]
    [InlineData("course")]
    [InlineData("assignment")]
    [InlineData("context")]
    [InlineData("instructions")]
    [InlineData("client")]
    [InlineData("alias")]
    [InlineData("cancelled")]
    [InlineData("cancelled_during_write")]
    public async Task Persisted_members_accept_only_siblings_in_the_same_scope(string mismatch)
    {
        var repository = new FakeReviewRepository();
        var run = Guid.NewGuid();
        var anchor = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1,
            gradingRunId: mismatch == "no_run" ? null : run);
        var sibling = AssistedGradingBatch.Create(mismatch == "course" ? 20 : 10,
            [mismatch == "assignment" ? 502 : 501], mismatch == "owner" ? "other" : "teacher-1", 321, 1,
            teacherInstructions: mismatch == "instructions" ? "Other instructions" : null,
            connectorClientId: mismatch == "client" ? "other" : null,
            connectionAlias: mismatch == "alias" ? "other" : null,
            moodleConnectionId: mismatch == "connection" ? "other" : null,
            gradingRunId: mismatch == "other_run" ? Guid.NewGuid() : mismatch == "no_run" ? null : run);
        repository.Batches.AddRange([anchor, sibling]);
        var first = AssistedGradingItem.Create(anchor.Id, 10, 501, 9001, 101, 1, "Ana");
        var second = AssistedGradingItem.Create(sibling.Id, sibling.CourseId, sibling.AssignmentIds[0], 9002, 102, 1, "Bia");
        first.MarkAwaitingAiAnalysis(null);
        second.MarkAwaitingAiAnalysis(null);
        repository.Items.AddRange([first, second]);
        if (mismatch is "mixed_context" or "real_context")
        {
            foreach (var item in repository.Items)
            {
                var context = GradingContext.Build(item.Id, item.BatchId, "10", "501", "101", "9001",
                    "Enunciado", "Criterio", null, 10m, null, "Entrega", [], null, null);
                var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
                item.RecordContextSnapshot(snapshot);
                if (item == first || mismatch == "real_context")
                    repository.ContextSnapshots[item.Id] = GradingContextSnapshotDocument.FromSnapshot(snapshot);
            }
            // Simulate a persisted member whose matching context hash is available
            // on the item, but whose snapshot row has not been loaded yet.
            if (mismatch == "mixed_context")
                typeof(AssistedGradingItem).GetProperty(nameof(AssistedGradingItem.ContextHash))!
                    .SetValue(second, first.ContextHash);
            else Assert.NotEqual(first.ContextHash, second.ContextHash);
        }
        var identity = GradingPackageIdentity.Create("teacher-1", null, null, null, 10, 501, null,
            [new GradingPackageItemIdentity(first.Id, null, null), new GradingPackageItemIdentity(second.Id, null, null)]);
        var package = GradingEvidencePackage.Create("teacher-1", null, null, null, 10, 501, null, 1,
            identity.PreparationKey, identity.ActivityKey, anchor.Id);
        var store = new FakePackageStore(package);
        store.Members.Add(CreateMember(package, first));
        store.Members.Add(GradingEvidencePackageMember.Create(package.Id, second.Id, second.MoodleUserId,
            second.StudentDisplayName, second.SubmissionId, second.AttemptNumber, null,
            mismatch == "context" ? new string('c', 64) : second.ContextHash, null));
        if (mismatch == "cancelled") sibling.Cancel();
        var blobStore = new FakeBlobStore
        {
            AfterWrite = mismatch == "cancelled_during_write" ? sibling.Cancel : null
        };
        var worker = CreateWorker(repository, store, blobStore: blobStore);

        if (mismatch is "cancelled" or "cancelled_during_write")
        {
            var cancelledResult = await worker.PrepareAsync(anchor.Id, package.Id, CancellationToken.None);
            Assert.Equal("Cancelled", Assert.Single(cancelledResult.Packages).Status);
            Assert.Null(package.BlobKey);
            if (mismatch == "cancelled_during_write") Assert.Single(blobStore.DeletedKeys);
            else Assert.Equal(0, package.AttemptCount);
            return;
        }

        if (mismatch is not ("same" or "mixed_context" or "real_context"))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.PrepareAsync(anchor.Id, package.Id, CancellationToken.None));
            Assert.Null(package.ManifestJson);
            Assert.Equal(0, package.AttemptCount);
            return;
        }

        await worker.PrepareAsync(anchor.Id, package.Id, CancellationToken.None);

        var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(package.ManifestJson!, GradingPackageContract.JsonOptions)!;
        Assert.Equal(2, manifest.Items.Count);
        Assert.Equal(anchor.Id, manifest.Items.Single(item => item.GradingItemId == first.Id).BatchJobId);
        Assert.Equal(sibling.Id, manifest.Items.Single(item => item.GradingItemId == second.Id).BatchJobId);
        Assert.Equal(anchor.Id, package.BatchJobId);
        if (mismatch == "real_context")
        {
            Assert.All(manifest.Items, item => Assert.True(item.ContextAvailable));
            Assert.NotEqual(first.ContextHash, manifest.Activity.ContextHash);
            Assert.DoesNotContain(manifest.Coverage.Issues, issue => issue.Code == "context_divergence");
        }
        if (mismatch == "mixed_context")
        {
            Assert.True(manifest.Items.Single(item => item.GradingItemId == first.Id).ContextAvailable);
            Assert.False(manifest.Items.Single(item => item.GradingItemId == second.Id).ContextAvailable);
            Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, package.Status);
        }
    }

    [Fact]
    public void OnlineTextArtifactSelection_reuses_the_artifact_with_the_current_hash()
    {
        var itemId = Guid.NewGuid();
        var oldArtifact = new GradingArtifact(
            Guid.NewGuid(),
            itemId,
            "submission_online_text",
            "texto-online.html",
            "text/html",
            new string('a', 64),
            12,
            ExtractionStatus.Succeeded,
            "texto antigo",
            "online_text",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var currentArtifact = new GradingArtifact(
            Guid.NewGuid(),
            itemId,
            "submission_online_text",
            "texto-online.html",
            "text/html",
            new string('b', 64),
            12,
            ExtractionStatus.Succeeded,
            "texto atual",
            "online_text",
            DateTimeOffset.UtcNow);

        var selected = GradingPackageWorker.FindOnlineTextArtifact(
            [oldArtifact, currentArtifact],
            new string('b', 64));

        Assert.Equal(currentArtifact.Id, selected?.Id);
    }

    [Fact]
    public void OnlineTextArtifactSelection_does_not_reuse_a_different_hash()
    {
        var artifact = new GradingArtifact(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "submission_online_text",
            "texto-online.html",
            "text/html",
            new string('a', 64),
            12,
            ExtractionStatus.Succeeded,
            "texto antigo",
            "online_text",
            DateTimeOffset.UtcNow);

        var selected = GradingPackageWorker.FindOnlineTextArtifact(
            [artifact],
            new string('b', 64));

        Assert.Null(selected);
    }

    [Fact]
    public void OnlineTextArtifactSelection_accepts_legacy_casing()
    {
        var artifact = new GradingArtifact(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "SUBMISSION_ONLINE_TEXT",
            "texto-online.html",
            "text/html",
            new string('a', 64),
            12,
            ExtractionStatus.Succeeded,
            "texto atual",
            "ONLINE_TEXT",
            DateTimeOffset.UtcNow);

        var selected = GradingPackageWorker.FindOnlineTextArtifact(
            [artifact],
            new string('a', 64));

        Assert.Equal(artifact.Id, selected?.Id);
    }

    [Fact]
    public void ExistingPackageFile_can_refresh_artifact_identity_when_path_is_reused()
    {
        var packageId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var original = GradingEvidencePackageFile.Create(
            packageId,
            itemId,
            Guid.NewGuid(),
            "entregas/ana/resposta.pdf",
            "resposta.pdf",
            "application/pdf",
            new string('a', 64),
            10,
            isContext: false);

        var replacementArtifactId = Guid.NewGuid();
        original.RefreshIdentity(
            itemId,
            replacementArtifactId,
            "entregas/ana/resposta.pdf",
            isContext: false);

        Assert.Equal(replacementArtifactId, original.ArtifactId);
        Assert.Equal(itemId, original.GradingItemId);
        Assert.Equal("entregas/ana/resposta.pdf", original.RelativePath);
    }

    [Fact]
    public async Task PrepareAsync_uses_a_unique_lease_owner_for_each_execution()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            2,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.Add(item);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
        var firstPackage = GradingEvidencePackage.Create(
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
            batch.Id);
        var secondPackage = GradingEvidencePackage.Create(
            identity.OwnerSubject,
            identity.MoodleConnectionId,
            identity.ConnectorClientId,
            identity.ConnectionAlias,
            identity.CourseId,
            identity.AssignmentId,
            identity.Cmid,
            2,
            identity.PreparationKey,
            identity.ActivityKey,
            batch.Id);
        var firstStore = new FakePackageStore(firstPackage) { RejectClaims = true };
        var secondStore = new FakePackageStore(secondPackage) { RejectClaims = true };
        firstStore.Members.Add(CreateMember(firstPackage, item));
        secondStore.Members.Add(CreateMember(secondPackage, item));

        var firstWorker = CreateWorker(repository, firstStore);
        var secondWorker = CreateWorker(repository, secondStore);

        await firstWorker.PrepareAsync(batch.Id, firstPackage.Id, CancellationToken.None);
        await secondWorker.PrepareAsync(batch.Id, secondPackage.Id, CancellationToken.None);

        Assert.NotEqual(
            Assert.Single(firstStore.ClaimedWorkerIds),
            Assert.Single(secondStore.ClaimedWorkerIds));
    }

    [Fact]
    public async Task PrepareAsync_uses_persisted_members_and_claims_before_reading_submissions()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            2,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var selected = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        selected.MarkAwaitingAiAnalysis(null);
        var context = GradingContext.Build(
            selected.Id,
            batch.Id,
            "10",
            "501",
            "101",
            "9001",
            "Enunciado da atividade",
            "Criterio",
            null,
            10m,
            null,
            "Texto persistido",
            [],
            null,
            null);
        var snapshot = GradingContextSnapshotFactory.Create(
            selected,
            context,
            new GradingContextOptions());
        selected.RecordContextSnapshot(snapshot);
        var newlyEligible = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 1, "Bia");
        newlyEligible.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.AddRange([selected, newlyEligible]);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            selected.Id,
            "submission_file",
            "resposta.txt",
            "text/plain",
            new string('a', 64),
            8,
            ExtractionStatus.Succeeded,
            "resposta persistida",
            null,
            DateTimeOffset.UtcNow));
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            selected.Id,
            "submission_file",
            "anexo.pdf",
            "application/pdf",
            new string('b', 64),
            12,
            ExtractionStatus.Succeeded,
            null,
            null,
            DateTimeOffset.UtcNow,
            "https://moodle.example/pluginfile.php/anexo.pdf"));
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            selected.Id,
            "assignment_context",
            "enunciado.pdf",
            "application/pdf",
            new string('c', 64),
            12,
            ExtractionStatus.Succeeded,
            null,
            null,
            DateTimeOffset.UtcNow,
            "https://moodle.example/pluginfile.php/enunciado.pdf"));
        repository.ContextSnapshots[selected.Id] = GradingContextSnapshotDocument.FromSnapshot(snapshot);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            selected.AssignmentId,
            null,
            [new GradingPackageItemIdentity(selected.Id, selected.ContextHash, selected.SubmissionContentHash)]);
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
            batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(GradingEvidencePackageMember.Create(
            package.Id,
            selected.Id,
            selected.MoodleUserId,
            selected.StudentDisplayName,
            selected.SubmissionId,
            selected.AttemptNumber,
            null,
            selected.ContextHash,
            selected.SubmissionContentHash));
        var submissions = new FakeSubmissionsGateway(packageStore);
        var blobStore = new FakeBlobStore();
        using var services = new ServiceCollection()
            .AddScoped<IGradingPackageStore>(_ => packageStore)
            .BuildServiceProvider();

        var worker = new GradingPackageWorker(
            repository,
            packageStore,
            blobStore,
            new CapturingArchiveWriter(),
            new TransientSubmissionFileGateway(),
            submissions,
            Options.Create(new GradingLimitsOptions { BatchLeaseMinutes = 1 }),
            NullLogger<GradingPackageWorker>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new GradingPackageDownloadLimiter());

        var result = await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(
            package.ManifestJson!,
            GradingPackageContract.JsonOptions);
        Assert.NotNull(manifest);
        Assert.Single(manifest!.Items);
        Assert.Equal(selected.Id, manifest.Items[0].GradingItemId);
        Assert.False(manifest.Items[0].SubmissionEvidenceComplete);
        Assert.False(manifest.Items[0].ContextAvailable);
        Assert.Contains(manifest.Coverage.Issues, issue => issue.Code == "context_unavailable");
        Assert.Contains(newlyEligible.Id, result.PendingItems);
        Assert.True(packageStore.WasClaimed);
        Assert.True(submissions.SawClaimedPackage);
        Assert.True(packageStore.RenewCalls >= 3);
        Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, package.Status);
    }

    [Fact]
    public async Task Background_worker_uses_persisted_members_without_loading_full_batch()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10, [501], "teacher-1", 321, 2,
            connectorClientId: "grading-client",
            connectionAlias: "moodle-a",
            moodleConnectionId: "connection-1");
        var selected = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Student A");
        selected.MarkAwaitingAiAnalysis(null);
        var unrelated = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 1, "Student B");
        unrelated.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.AddRange([selected, unrelated]);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            selected.AssignmentId,
            null,
            [new GradingPackageItemIdentity(selected.Id, selected.ContextHash, selected.SubmissionContentHash)]);
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
            batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, selected));
        var worker = CreateWorker(repository, packageStore);
        var channel = new GradingPackagePreparationChannel();
        var admissionWorkScope = new CapturingAdmissionWorkScope();
        var serviceProvider = new ServiceCollection()
            .AddScoped<IGradingReviewRepository>(_ => repository)
            .AddScoped<GradingPackageWorker>(_ => worker)
            .AddScoped<IConnectorExecutionContext>(_ => new ConnectorExecutionContext())
            .AddSingleton<IMoodleAdmissionWorkScope>(admissionWorkScope)
            .BuildServiceProvider();
        var service = new GradingPackageWorkerService(
            channel,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GradingLimitsOptions
            {
                GradingPackageWorkerConcurrency = 1,
                GradingPackagePollSeconds = 300
            }),
            NullLogger<GradingPackageWorkerService>.Instance,
            admissionWorkScope);
        Assert.True(channel.TryEnqueue(batch.Id, package.Id));

        await service.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (package.Status is GradingEvidencePackageStatus.Queued or GradingEvidencePackageStatus.Preparing)
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stopTimeout.Token);
            await serviceProvider.DisposeAsync();
        }

        Assert.Equal(0, repository.ListItemsByBatchCalls);
        Assert.True(package.Status is GradingEvidencePackageStatus.Ready or GradingEvidencePackageStatus.PartiallyReady);
        Assert.Equal(MoodleAdmissionOrigin.Grading, admissionWorkScope.LastOrigin);
        Assert.Equal("grading-client", admissionWorkScope.LastOwnerId);
        Assert.Equal(MoodleAdmissionPriority.Background, admissionWorkScope.LastPriority);
    }

    [Fact]
    public async Task PrepareAsync_loads_member_batches_with_one_bulk_lookup()
    {
        var repository = new FakeReviewRepository();
        var runId = Guid.NewGuid();
        var batches = new List<AssistedGradingBatch>();
        var items = new List<AssistedGradingItem>();
        for (var index = 0; index < 20; index++)
        {
            var batch = AssistedGradingBatch.Create(
                10, [501], "teacher-1", 321, 1, gradingRunId: runId);
            var item = AssistedGradingItem.Create(
                batch.Id, 10, 501, 9001 + index, 101 + index, 1, $"Student {index + 1}");
            item.MarkAwaitingAiAnalysis(null);
            batches.Add(batch);
            items.Add(item);
        }
        repository.Batches.AddRange(batches);
        repository.Items.AddRange(items);

        var anchor = batches[0];
        var identity = GradingPackageIdentity.Create(
            anchor.CreatedBySubject,
            anchor.MoodleConnectionId,
            anchor.ConnectorClientId,
            anchor.ConnectionAlias,
            anchor.CourseId,
            501,
            null,
            items.Select(item => new GradingPackageItemIdentity(
                item.Id, item.ContextHash, item.SubmissionContentHash)).ToArray());
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
            anchor.Id);
        var packageStore = new FakePackageStore(package);
        foreach (var item in items)
        {
            packageStore.Members.Add(CreateMember(package, item));
        }
        var worker = CreateWorker(repository, packageStore);

        var result = await worker.PrepareForBackgroundAsync(anchor.Id, package.Id, CancellationToken.None);

        Assert.True(repository.GetBatchesAsyncCalls == 1);
        Assert.True(repository.LastBatchIds.Count == 19);
        Assert.Equal(19, repository.LastBatchIds.Distinct().Count());
        Assert.Single(result.Packages);
        Assert.True(package.Status is GradingEvidencePackageStatus.Ready or GradingEvidencePackageStatus.PartiallyReady);
    }

    [Fact]
    public async Task PrepareAsync_blocks_missing_member_batch_before_claiming_package()
    {
        var repository = new FakeReviewRepository();
        var anchor = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1);
        var missingBatch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1);
        var anchorItem = AssistedGradingItem.Create(anchor.Id, 10, 501, 9001, 101, 1, "Student A");
        var missingItem = AssistedGradingItem.Create(missingBatch.Id, 10, 501, 9002, 102, 1, "Student B");
        anchorItem.MarkAwaitingAiAnalysis(null);
        missingItem.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(anchor);
        repository.Items.AddRange([anchorItem, missingItem]);
        var identity = GradingPackageIdentity.Create(
            anchor.CreatedBySubject,
            anchor.MoodleConnectionId,
            anchor.ConnectorClientId,
            anchor.ConnectionAlias,
            anchor.CourseId,
            501,
            null,
            [
                new GradingPackageItemIdentity(anchorItem.Id, anchorItem.ContextHash, anchorItem.SubmissionContentHash),
                new GradingPackageItemIdentity(missingItem.Id, missingItem.ContextHash, missingItem.SubmissionContentHash)
            ]);
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
            anchor.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, anchorItem));
        packageStore.Members.Add(CreateMember(package, missingItem));
        var worker = CreateWorker(repository, packageStore);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.PrepareForBackgroundAsync(anchor.Id, package.Id, CancellationToken.None));

        Assert.Contains("nao existe mais", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(repository.GetBatchesAsyncCalls == 1);
        Assert.False(packageStore.WasClaimed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_does_not_reload_batch_items_when_requested_package_is_already_finalized(
        bool resultsWereReceived)
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1);
        repository.Batches.Add(batch);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            501,
            null,
            [new GradingPackageItemIdentity(Guid.NewGuid(), null, null)]);
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
            batch.Id);
        var now = DateTimeOffset.UtcNow;
        package.MarkManifest("{}", new string('a', 64), now);
        package.MarkReady("packages/final.zip", new string('b', 64), 10, 20, 1, 1, 1, 0, now);
        if (resultsWereReceived)
        {
            package.MarkResultsReceived(now.AddSeconds(1));
        }
        var packageStore = new FakePackageStore(package);
        var worker = CreateWorker(repository, packageStore);

        var result = await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(0, repository.ListItemsByBatchCalls);
        Assert.Equal(0, packageStore.ListMembersCalls);
        Assert.False(packageStore.WasClaimed);
        Assert.Equal(package.Id, Assert.Single(result.ReusedPackageIds));
        Assert.Equal(package.Id, Assert.Single(result.Packages).PackageId);
        Assert.Equal(
            (resultsWereReceived
                ? GradingEvidencePackageStatus.ResultsReceived
                : GradingEvidencePackageStatus.Ready).ToString(),
            result.Packages[0].Status);
    }

    [Fact]
    public async Task PrepareAsync_carrega_submissoes_de_todos_os_chunks_da_mesma_atividade()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            2,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        repository.Batches.Add(batch);
        var items = Enumerable.Range(1, 2)
            .Select(index =>
            {
                var item = AssistedGradingItem.Create(
                    batch.Id,
                    10,
                    501,
                    9000 + index,
                    100 + index,
                    1,
                    $"Aluno {index}");
                item.MarkAwaitingAiAnalysis(null);
                var context = GradingContext.Build(
                    item.Id,
                    batch.Id,
                    "10",
                    "501",
                    item.SubmissionId?.ToString(),
                    item.MoodleUserId.ToString(),
                    "Enunciado da atividade",
                    "Criterio",
                    null,
                    10m,
                    null,
                    "Texto persistido",
                    [],
                    null,
                    null);
                var snapshot = GradingContextSnapshotFactory.Create(
                    item,
                    context,
                    new GradingContextOptions());
                item.RecordContextSnapshot(snapshot);
                repository.ContextSnapshots[item.Id] = GradingContextSnapshotDocument.FromSnapshot(snapshot);
                return item;
            })
            .ToArray();
        repository.Items.AddRange(items);

        var firstIdentity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            items[0].AssignmentId,
            null,
            [new GradingPackageItemIdentity(items[0].Id, items[0].ContextHash, items[0].SubmissionContentHash)]);
        var firstPackage = GradingEvidencePackage.Create(
            firstIdentity.OwnerSubject,
            firstIdentity.MoodleConnectionId,
            firstIdentity.ConnectorClientId,
            firstIdentity.ConnectionAlias,
            firstIdentity.CourseId,
            firstIdentity.AssignmentId,
            firstIdentity.Cmid,
            1,
            firstIdentity.PreparationKey,
            firstIdentity.ActivityKey,
            batch.Id);
        var packageStore = new FakePackageStore(firstPackage)
        {
            CreatePackagePerIdentity = true
        };
        var submissions = new AllOnlineTextSubmissionsGateway();
        var worker = CreateWorker(
            repository,
            packageStore,
            submissionsGateway: submissions,
            limits: new GradingLimitsOptions
            {
                BatchLeaseMinutes = 1,
                MaxGradingPackageItems = 1
            });

        await worker.PrepareAsync(batch.Id, null, CancellationToken.None);

        var preparedPackages = packageStore.Packages
            .Where(package => package.Status is GradingEvidencePackageStatus.Ready or GradingEvidencePackageStatus.PartiallyReady)
            .ToArray();
        Assert.Equal(2, preparedPackages.Length);
        Assert.Equal(1, submissions.BatchReadCalls);
        foreach (var package in preparedPackages)
        {
            var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(
                package.ManifestJson!,
                GradingPackageContract.JsonOptions);
            var item = Assert.Single(manifest!.Items);
            Assert.Contains(item.Files, file => file.OriginalName == "texto-online.html");
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task Separate_package_workers_share_complete_submission_read_only_when_enabled(
        bool reuseEnabled,
        int expectedBatchReadCalls)
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10, [501], "synthetic-teacher", 321, 2,
            connectorClientId: "client-a", connectionAlias: "alias-a", moodleConnectionId: "connection-a");
        repository.Batches.Add(batch);
        var items = Enumerable.Range(1, 2).Select(index =>
        {
            var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9000 + index, 100 + index, 1);
            item.MarkAwaitingAiAnalysis(null);
            var context = GradingContext.Build(item.Id, batch.Id, "10", "501",
                item.SubmissionId?.ToString(), item.MoodleUserId.ToString(),
                "Synthetic assignment", "Synthetic criterion", null, 10m, null,
                "Synthetic persisted answer", [], null, null);
            var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
            item.RecordContextSnapshot(snapshot);
            repository.ContextSnapshots[item.Id] = GradingContextSnapshotDocument.FromSnapshot(snapshot);
            return item;
        }).ToArray();
        repository.Items.AddRange(items);

        GradingPackageIdentity CreateIdentity(AssistedGradingItem item) => GradingPackageIdentity.Create(
            batch.CreatedBySubject, batch.MoodleConnectionId, batch.ConnectorClientId, batch.ConnectionAlias,
            batch.CourseId, item.AssignmentId, null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);

        var firstIdentity = CreateIdentity(items[0]);
        var firstPackage = GradingEvidencePackage.Create(
            firstIdentity.OwnerSubject, firstIdentity.MoodleConnectionId, firstIdentity.ConnectorClientId,
            firstIdentity.ConnectionAlias, firstIdentity.CourseId, firstIdentity.AssignmentId, firstIdentity.Cmid,
            1, firstIdentity.PreparationKey, firstIdentity.ActivityKey, batch.Id);
        var packageStore = new FakePackageStore(firstPackage) { CreatePackagePerIdentity = true };
        packageStore.Members.Add(CreateMember(firstPackage, items[0]));
        var secondPackage = await packageStore.GetOrCreateAsync(CreateIdentity(items[1]), batch.Id, CancellationToken.None);
        packageStore.Members.Add(CreateMember(secondPackage, items[1]));

        var limits = new GradingLimitsOptions
        {
            BatchLeaseMinutes = 1,
            GradingPackageSubmissionReuseEnabled = reuseEnabled
        };
        using var submissionCache = new GradingPackageSubmissionReadCache(
            Options.Create(limits), TimeProvider.System);
        const string privateStudentText = "synthetic-private-student-answer";
        const string privateFileUrl = "https://moodle.invalid/pluginfile.php/private-token";
        const string privateFilename = "synthetic-private-answer.pdf";
        var submissions = new AllOnlineTextSubmissionsGateway(
            privateStudentText,
            new AssignmentSubmissionFile(privateFilename, "application/pdf", 12, privateFileUrl));
        var logger = new CapturingLogger<GradingPackageWorker>();
        var telemetry = new CapturingGradingTelemetry();
        var worker = CreateWorker(repository, packageStore,
            submissionsGateway: submissions, limits: limits, submissionReadCache: submissionCache,
            logger: logger, telemetry: telemetry);

        var firstResult = await worker.PrepareAsync(batch.Id, firstPackage.Id, CancellationToken.None);
        var secondResult = await worker.PrepareAsync(batch.Id, secondPackage.Id, CancellationToken.None);

        Assert.Single(firstResult.Packages);
        Assert.Single(secondResult.Packages);
        Assert.Equal(expectedBatchReadCalls, submissions.BatchReadCalls);
        Assert.All(new[] { firstPackage, secondPackage }, package =>
            Assert.Contains(package.Status,
                new[] { GradingEvidencePackageStatus.Ready, GradingEvidencePackageStatus.PartiallyReady }));
        var logs = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("grading_package", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(privateStudentText, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(privateFileUrl, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(privateFilename, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(firstPackage.Id.ToString(), logs, StringComparison.Ordinal);
        var privateTextBytes = Encoding.UTF8.GetBytes(privateStudentText);
        var privateTextHash = Convert.ToHexString(SHA256.HashData(privateTextBytes)).ToLowerInvariant();
        foreach (var package in new[] { firstPackage, secondPackage })
        {
            var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(
                package.ManifestJson!, GradingPackageContract.JsonOptions);
            Assert.NotNull(manifest);
            Assert.Equal(package.ManifestHash, GradingPackageManifestHash.Compute(manifest!));
            var onlineTextFile = Assert.Single(Assert.Single(manifest!.Items).Files,
                file => file.OriginalName == "texto-online.html");
            Assert.Equal(privateTextHash, onlineTextFile.Sha256);
            Assert.Equal(privateTextBytes.LongLength, onlineTextFile.SizeBytes);
        }
        Assert.Equal(2, telemetry.SubmissionReads.Count);
        Assert.Equal(expectedBatchReadCalls, telemetry.SubmissionReads.Sum(read => read.GatewayCallCount));
        if (reuseEnabled)
        {
            Assert.Contains(telemetry.SubmissionReads,
                read => read.CacheResult == "miss" && read.GatewayCallCount == 1);
            Assert.Contains(telemetry.SubmissionReads,
                read => read.CacheResult == "hit" && read.GatewayCallCount == 0);
        }
        else
        {
            Assert.All(telemetry.SubmissionReads, read => Assert.Equal("bypass", read.CacheResult));
        }
    }

    [Fact]
    public async Task PrepareAsync_requeues_versioned_item_when_context_snapshot_is_missing()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            2,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            item.SubmissionId?.ToString(),
            item.MoodleUserId.ToString(),
            "Enunciado da atividade",
            "Criterio",
            null,
            10m,
            null,
            "Texto persistido",
            [],
            null,
            null);
        var snapshot = GradingContextSnapshotFactory.Create(
            item,
            context,
            new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        repository.Batches.Add(batch);
        repository.Items.Add(item);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "resposta.txt",
            "text/plain",
            new string('a', 64),
            8,
            ExtractionStatus.Succeeded,
            "resposta persistida",
            null,
            DateTimeOffset.UtcNow));

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
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
            batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, item));
        var blobStore = new FakeBlobStore();
        var worker = CreateWorker(repository, packageStore, blobStore: blobStore);

        await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(GradingEvidencePackageStatus.Queued, package.Status);
        Assert.Equal("package_context_unavailable", package.LastErrorCode);
        Assert.NotNull(package.NextAttemptAt);
        Assert.Null(package.ManifestJson);
        Assert.Null(blobStore.LastWrite);
    }

    [Fact]
    public async Task PrepareAsync_legado_tambem_particiona_por_limite_de_entradas()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 2);
        repository.Batches.Add(batch);
        var items = Enumerable.Range(1, 2)
            .Select(index =>
            {
                var item = AssistedGradingItem.Create(
                    batch.Id,
                    10,
                    501,
                    9000 + index,
                    100 + index,
                    1,
                    $"Aluno {index}");
                item.MarkAwaitingAiAnalysis(null);
                repository.Artifacts.Add(new GradingArtifact(
                    Guid.NewGuid(),
                    item.Id,
                    "submission_file",
                    $"resposta-{index}.txt",
                    "text/plain",
                    new string('a', 64),
                    8,
                    ExtractionStatus.Succeeded,
                    "resposta persistida",
                    null,
                    DateTimeOffset.UtcNow));
                return item;
            })
            .ToArray();
        repository.Items.AddRange(items);

        var firstIdentity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            items[0].AssignmentId,
            null,
            [new GradingPackageItemIdentity(items[0].Id, items[0].ContextHash, items[0].SubmissionContentHash)]);
        var firstPackage = GradingEvidencePackage.Create(
            firstIdentity.OwnerSubject,
            firstIdentity.MoodleConnectionId,
            firstIdentity.ConnectorClientId,
            firstIdentity.ConnectionAlias,
            firstIdentity.CourseId,
            firstIdentity.AssignmentId,
            firstIdentity.Cmid,
            1,
            firstIdentity.PreparationKey,
            firstIdentity.ActivityKey,
            batch.Id);
        var packageStore = new FakePackageStore(firstPackage)
        {
            CreatePackagePerIdentity = true
        };
        var worker = CreateWorker(
            repository,
            packageStore,
            limits: new GradingLimitsOptions
            {
                BatchLeaseMinutes = 1,
                MaxGradingPackageItems = 20,
                MaxGradingPackageEntries = 4,
                MaxGradingPackageZipMb = 1,
                MaxGradingPackageUncompressedMb = 1,
                MaxFileSizeMb = 1,
                MaxTextCharsPerSubmission = 1
            });

        await worker.PrepareAsync(batch.Id, null, CancellationToken.None);

        var preparedPackages = packageStore.Packages
            .Where(package => package.Status is GradingEvidencePackageStatus.Ready or GradingEvidencePackageStatus.PartiallyReady)
            .ToArray();
        Assert.Equal(2, preparedPackages.Length);
        Assert.All(preparedPackages, package =>
        {
            var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(
                package.ManifestJson!,
                GradingPackageContract.JsonOptions);
            Assert.Single(manifest!.Items);
        });
    }

    [Fact]
    public async Task Oversized_submission_marks_only_its_member_pending_and_keeps_sibling_evidence()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10, [501], "teacher-1", 321, 2,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        repository.Batches.Add(batch);
        var oversizedItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Synthetic 1");
        var normalItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 1, "Synthetic 2");
        oversizedItem.MarkAwaitingAiAnalysis(null);
        normalItem.MarkAwaitingAiAnalysis(null);
        repository.Items.AddRange(new[] { oversizedItem, normalItem });
        foreach (var item in repository.Items)
        {
            var context = GradingContext.Build(
                item.Id, item.BatchId, "10", "501", item.SubmissionId?.ToString(),
                item.MoodleUserId.ToString(), "Synthetic assignment instructions", "Synthetic criterion",
                null, 10m, null, "Synthetic response", [], null, null, gradingMode: "numeric");
            var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
            item.RecordContextSnapshot(snapshot);
            repository.ContextSnapshots[item.Id] = GradingContextSnapshotDocument.FromSnapshot(snapshot);
        }

        var oversizedBytes = new byte[(1024 * 1024) + 1];
        Array.Fill(oversizedBytes, (byte)0x5a);
        var oversizedHash = Convert.ToHexString(SHA256.HashData(oversizedBytes)).ToLowerInvariant();
        const string oversizedUrl = "https://moodle.example/synthetic/oversized.bin";
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(), oversizedItem.Id, "submission_file", "oversized.bin", "application/octet-stream",
            oversizedHash, oversizedBytes.LongLength, ExtractionStatus.Succeeded,
            null, null, DateTimeOffset.UtcNow, oversizedUrl));
        var normalContent = "synthetic sibling evidence";
        var normalBytes = Encoding.UTF8.GetBytes(normalContent);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(), normalItem.Id, "submission_file", "answer.txt", "text/plain",
            Convert.ToHexString(SHA256.HashData(normalBytes)).ToLowerInvariant(), normalBytes.LongLength,
            ExtractionStatus.Succeeded, normalContent, null, DateTimeOffset.UtcNow));

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            batch.AssignmentIds[0],
            null,
            new[] { oversizedItem, normalItem }.Select(item =>
                new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)).ToArray());
        var package = GradingEvidencePackage.Create(
            identity.OwnerSubject, identity.MoodleConnectionId, identity.ConnectorClientId,
            identity.ConnectionAlias, identity.CourseId, identity.AssignmentId, identity.Cmid,
            1, identity.PreparationKey, identity.ActivityKey, batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, oversizedItem));
        packageStore.Members.Add(CreateMember(package, normalItem));
        var records = new[]
        {
            new AssignmentSubmissionRecord("9001", "101", "submitted", null, null,
                DateTimeOffset.UtcNow, 1, 1, false),
            new AssignmentSubmissionRecord("9002", "102", "submitted", null, null,
                DateTimeOffset.UtcNow, 1, 1, false)
        };
        var worker = CreateWorker(
            repository,
            packageStore,
            submissionsGateway: new SyntheticAssignmentSubmissionsGateway(records),
            limits: new GradingLimitsOptions
            {
                BatchLeaseMinutes = 1,
                MaxGradingPackageItems = 20,
                MaxGradingPackageEntries = 16,
                MaxGradingPackageZipMb = 2,
                MaxGradingPackageUncompressedMb = 2,
                MaxFileSizeMb = 1,
                MaxTextCharsPerSubmission = 100
            },
            submissionFileGateway: new SyntheticFileDownloadGateway(oversizedBytes, oversizedHash));

        await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, package.Status);
        var manifest = JsonSerializer.Deserialize<GradingPackageManifest>(
            package.ManifestJson!, GradingPackageContract.JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Coverage.SelectedItems);
        Assert.Equal(1, manifest.Coverage.IncludedItems);
        Assert.Equal(1, manifest.Coverage.PendingItems);
        Assert.Equal(new[] { oversizedItem.Id, normalItem.Id }.Order(),
            manifest.Items.Select(item => item.GradingItemId).Order());
        Assert.False(manifest.Items.Single(item => item.GradingItemId == oversizedItem.Id).SubmissionEvidenceComplete);
        Assert.True(manifest.Items.Single(item => item.GradingItemId == normalItem.Id).SubmissionEvidenceComplete);
        Assert.Contains(manifest.Coverage.Issues, issue =>
            issue.GradingItemId == oversizedItem.Id && issue.Code == "artifact_read_failed");
        Assert.Equal(GradingEvidencePackageMemberStatus.Failed,
            packageStore.Members.Single(member => member.GradingItemId == oversizedItem.Id).Status);
        Assert.Equal(GradingEvidencePackageMemberStatus.Included,
            packageStore.Members.Single(member => member.GradingItemId == normalItem.Id).Status);
    }

    private static GradingEvidencePackageMember CreateMember(
        GradingEvidencePackage package,
        AssistedGradingItem item) =>
        GradingEvidencePackageMember.Create(
            package.Id,
            item.Id,
            item.MoodleUserId,
            item.StudentDisplayName,
            item.SubmissionId,
            item.AttemptNumber,
            null,
            item.ContextHash,
            item.SubmissionContentHash);

    private static GradingPackageWorker CreateWorker(
        FakeReviewRepository repository,
        FakePackageStore packageStore,
        IMoodleAssignmentSubmissionsGateway? submissionsGateway = null,
        IGradingPackageBlobStore? blobStore = null,
        GradingLimitsOptions? limits = null,
        IMoodleSubmissionFileGateway? submissionFileGateway = null,
        IGradingPackageArtifactCache? artifactCache = null,
        GradingPackageSubmissionReadCache? submissionReadCache = null,
        ILogger<GradingPackageWorker>? logger = null,
        IGradingOperationTelemetry? telemetry = null)
    {
        var services = new ServiceCollection()
            .AddScoped<IGradingPackageStore>(_ => packageStore)
            .BuildServiceProvider();
        return new GradingPackageWorker(
            repository,
            packageStore,
            blobStore ?? new FakeBlobStore(),
            new CapturingArchiveWriter(),
            submissionFileGateway ?? new TransientSubmissionFileGateway(),
            submissionsGateway ?? new FakeSubmissionsGateway(packageStore),
            Options.Create(limits ?? new GradingLimitsOptions { BatchLeaseMinutes = 1 }),
            logger ?? NullLogger<GradingPackageWorker>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new GradingPackageDownloadLimiter(),
            telemetry,
            artifactCache: artifactCache,
            submissionReadCache: submissionReadCache);
    }

    [Fact]
    public async Task PrepareAsync_materializes_different_students_concurrently_with_global_limit()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            3,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        repository.Batches.Add(batch);
        var items = Enumerable.Range(1, 3)
            .Select(index =>
            {
                var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9000 + index, 100 + index, 1, $"Aluno {index}");
                item.MarkAwaitingAiAnalysis(null);
                return item;
            })
            .ToArray();
        repository.Items.AddRange(items);
        var content = Encoding.UTF8.GetBytes("entrega");
        var contentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        foreach (var item in items)
        {
            repository.Artifacts.Add(new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "submission_file",
                $"resposta-{item.Id:N}.txt",
                "text/plain",
                contentHash,
                content.LongLength,
                ExtractionStatus.Succeeded,
                null,
                null,
                DateTimeOffset.UtcNow,
                $"https://moodle.example/pluginfile.php/{item.Id:N}.txt"));
        }

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            501,
            null,
            items.Select(item => new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)).ToArray());
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
            batch.Id);
        var packageStore = new FakePackageStore(package);
        foreach (var item in items)
        {
            packageStore.Members.Add(GradingEvidencePackageMember.Create(
                package.Id,
                item.Id,
                item.MoodleUserId,
                item.StudentDisplayName,
                item.SubmissionId,
                item.AttemptNumber,
                null,
                item.ContextHash,
                item.SubmissionContentHash));
        }

        var gateway = new ConcurrentSubmissionFileGateway(content, contentHash);
        var submissions = new FakeSubmissionsGateway(packageStore);
        using var services = new ServiceCollection()
            .AddScoped<IGradingPackageStore>(_ => packageStore)
            .BuildServiceProvider();
        var worker = new GradingPackageWorker(
            repository,
            packageStore,
            new FakeBlobStore(),
            new CapturingArchiveWriter(),
            gateway,
            submissions,
            Options.Create(new GradingLimitsOptions
            {
                BatchLeaseMinutes = 1,
                GradingPackageDownloadConcurrency = 3,
                MaxConcurrentDownloadsPerConnection = 2
            }),
            NullLogger<GradingPackageWorker>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new GradingPackageDownloadLimiter());

        await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(2, gateway.MaxConcurrency);
        Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, package.Status);
    }

    [Fact]
    public async Task PrepareAsync_reuses_verified_artifact_cache_on_retry()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.Add(item);

        var firstContent = Encoding.UTF8.GetBytes("anexo-a");
        var secondContent = Encoding.UTF8.GetBytes("anexo-b");
        var firstUrl = "https://moodle.example/pluginfile.php/anexo-a.pdf";
        var secondUrl = "https://moodle.example/pluginfile.php/anexo-b.pdf";
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "anexo-a.pdf",
            "application/pdf",
            Convert.ToHexString(SHA256.HashData(firstContent)).ToLowerInvariant(),
            firstContent.LongLength,
            ExtractionStatus.Succeeded,
            null,
            null,
            DateTimeOffset.UtcNow,
            firstUrl));
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "anexo-b.pdf",
            "application/pdf",
            Convert.ToHexString(SHA256.HashData(secondContent)).ToLowerInvariant(),
            secondContent.LongLength,
            ExtractionStatus.Succeeded,
            null,
            null,
            DateTimeOffset.UtcNow,
            secondUrl));

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
        var firstPackage = GradingEvidencePackage.Create(
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
            batch.Id);
        var secondPackage = GradingEvidencePackage.Create(
            identity.OwnerSubject,
            identity.MoodleConnectionId,
            identity.ConnectorClientId,
            identity.ConnectionAlias,
            identity.CourseId,
            identity.AssignmentId,
            identity.Cmid,
            2,
            identity.PreparationKey,
            identity.ActivityKey,
            batch.Id);
        var packageStore = new FakePackageStore(firstPackage);
        packageStore.Packages.Add(secondPackage);
        packageStore.Members.Add(CreateMember(firstPackage, item));
        packageStore.Members.Add(CreateMember(secondPackage, item));

        var gateway = new RetryAwareSubmissionFileGateway(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [firstUrl] = firstContent,
                [secondUrl] = secondContent
            },
            secondUrl);
        var cache = new InMemoryArtifactCache();
        var worker = CreateWorker(
            repository,
            packageStore,
            new FakeSubmissionsGateway(packageStore),
            new FakeBlobStore(),
            submissionFileGateway: gateway,
            artifactCache: cache);

        await worker.PrepareAsync(batch.Id, firstPackage.Id, CancellationToken.None);
        await worker.PrepareAsync(batch.Id, secondPackage.Id, CancellationToken.None);

        Assert.Equal(1, gateway.Calls[firstUrl]);
        Assert.Equal(2, gateway.Calls[secondUrl]);
        Assert.Equal(1, cache.HitCount);
        Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, firstPackage.Status);
        Assert.Equal(GradingEvidencePackageStatus.PartiallyReady, secondPackage.Status);
    }

    [Fact]
    public async Task PrepareAsync_continues_when_artifact_cache_is_unavailable()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.Add(item);

        var content = Encoding.UTF8.GetBytes("anexo-resiliente");
        var url = "https://moodle.example/pluginfile.php/anexo-resiliente.pdf";
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "anexo-resiliente.pdf",
            "application/pdf",
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            content.LongLength,
            ExtractionStatus.Succeeded,
            null,
            null,
            DateTimeOffset.UtcNow,
            url));

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
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
            batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, item));

        var gateway = new RetryAwareSubmissionFileGateway(
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { [url] = content },
            "https://moodle.example/pluginfile.php/never-fails.pdf");
        var worker = CreateWorker(
            repository,
            packageStore,
            new FakeSubmissionsGateway(packageStore),
            submissionFileGateway: gateway,
            artifactCache: new ThrowingArtifactCache());

        await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(1, gateway.Calls[url]);
        Assert.True(package.Status is
            GradingEvidencePackageStatus.Ready or
            GradingEvidencePackageStatus.PartiallyReady);
        Assert.NotNull(package.ManifestJson);
    }

    [Fact]
    public async Task PrepareAsync_requeues_wrapped_transient_submission_read()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.Add(item);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
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
            batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, item));
        var submissions = new FakeSubmissionsGateway(packageStore)
        {
            ThrowTransientBatchRead = true
        };
        var worker = CreateWorker(repository, packageStore, submissions);

        await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(GradingEvidencePackageStatus.Queued, package.Status);
        Assert.Equal("package_submission_read_transient", package.LastErrorCode);
        Assert.NotNull(package.NextAttemptAt);
    }

    [Fact]
    public async Task PrepareAsync_persistsHttp429RetryAfterWithoutInlineRetryOrDraftChange()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.SetDraft(8m, 0.8m, "Rascunho sintético salvo antes do limite.");
        var draftHash = GradingDraftVersionHash.Compute(item);
        repository.Batches.Add(batch);
        repository.Items.Add(item);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
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
            batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, item));
        var submissions = new FakeSubmissionsGateway(packageStore)
        {
            BatchReadFailure = new MoodleApiException(
                MoodleErrorContract.ApiError,
                "limite temporário",
                httpStatusCode: 429,
                retryAfter: TimeSpan.FromSeconds(7))
        };
        var worker = CreateWorker(repository, packageStore, submissions);
        var startedAt = DateTimeOffset.UtcNow;

        await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(1, submissions.BatchReadCalls);
        Assert.Equal(GradingEvidencePackageStatus.Queued, package.Status);
        Assert.Equal("http_429", package.LastErrorCode);
        Assert.NotNull(package.NextAttemptAt);
        Assert.InRange(package.NextAttemptAt.Value, startedAt.AddSeconds(7), DateTimeOffset.UtcNow.AddSeconds(8));
        Assert.Equal(draftHash, GradingDraftVersionHash.Compute(item));
    }

    [Fact]
    public async Task PrepareAsync_requeues_transient_storage_failure()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.Add(item);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
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
            batch.Id);
        var packageStore = new FakePackageStore(package);
        packageStore.Members.Add(CreateMember(package, item));
        var worker = CreateWorker(
            repository,
            packageStore,
            new FakeSubmissionsGateway(packageStore),
            new FakeBlobStore { ThrowOnWrite = true });

        await worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None);

        Assert.Equal(GradingEvidencePackageStatus.Queued, package.Status);
        Assert.Equal("package_transient_dependency", package.LastErrorCode);
        Assert.NotNull(package.NextAttemptAt);
    }

    [Fact]
    public async Task PrepareAsync_keeps_archive_when_final_save_result_is_ambiguous_but_state_is_persisted()
    {
        var repository = new FakeReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            1,
            connectorClientId: "client-a",
            connectionAlias: "alias-a",
            moodleConnectionId: "connection-a");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana");
        item.MarkAwaitingAiAnalysis(null);
        repository.Batches.Add(batch);
        repository.Items.Add(item);

        var identity = GradingPackageIdentity.Create(
            batch.CreatedBySubject,
            batch.MoodleConnectionId,
            batch.ConnectorClientId,
            batch.ConnectionAlias,
            batch.CourseId,
            item.AssignmentId,
            null,
            [new GradingPackageItemIdentity(item.Id, item.ContextHash, item.SubmissionContentHash)]);
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
            batch.Id);
        var packageStore = new FakePackageStore(package)
        {
            ThrowAfterFinalSave = true
        };
        packageStore.Members.Add(CreateMember(package, item));
        var blobStore = new FakeBlobStore();
        var worker = CreateWorker(
            repository,
            packageStore,
            new FakeSubmissionsGateway(packageStore),
            blobStore);

        await Assert.ThrowsAsync<IOException>(() =>
            worker.PrepareAsync(batch.Id, package.Id, CancellationToken.None));

        Assert.NotNull(blobStore.LastWrite);
        Assert.Empty(blobStore.DeletedKeys);
        Assert.Equal(blobStore.LastWrite!.BlobKey, package.BlobKey);
        Assert.Contains(
            package.Status,
            new[]
            {
                GradingEvidencePackageStatus.Ready,
                GradingEvidencePackageStatus.PartiallyReady
            });
    }

    private sealed class FakePackageStore(GradingEvidencePackage package) : IGradingPackageStore
    {
        public List<GradingEvidencePackage> Packages { get; } = [package];
        public List<GradingEvidencePackageMember> Members { get; } = [];
        public List<GradingEvidencePackageFile> Files { get; } = [];
        public bool WasClaimed { get; private set; }
        public int ListMembersCalls { get; private set; }
        public int RenewCalls { get; private set; }
        public bool RejectClaims { get; init; }
        public bool CreatePackagePerIdentity { get; init; }
        public bool ThrowAfterFinalSave { get; init; }
        public List<string> ClaimedWorkerIds { get; } = [];
        private bool finalSaveThrown;

        public Task<GradingEvidencePackage?> GetAsync(Guid packageId, CancellationToken cancellationToken) =>
            Task.FromResult(Packages.SingleOrDefault(item => item.Id == packageId));

        public Task<GradingEvidencePackage?> FindReusableAsync(GradingPackageIdentity identity, CancellationToken cancellationToken) =>
            Task.FromResult<GradingEvidencePackage?>(null);

        public Task<GradingEvidencePackage> GetOrCreateAsync(GradingPackageIdentity identity, Guid? batchJobId, CancellationToken cancellationToken)
        {
            if (!CreatePackagePerIdentity)
            {
                return Task.FromResult(Packages.Single());
            }

            var existing = Packages.FirstOrDefault(package => package.PreparationKey == identity.PreparationKey);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var version = Packages
                .Where(package => package.ActivityKey == identity.ActivityKey)
                .Select(package => package.PackageVersion)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var created = GradingEvidencePackage.Create(
                identity.OwnerSubject,
                identity.MoodleConnectionId,
                identity.ConnectorClientId,
                identity.ConnectionAlias,
                identity.CourseId,
                identity.AssignmentId,
                identity.Cmid,
                version,
                identity.PreparationKey,
                identity.ActivityKey,
                batchJobId);
            Packages.Add(created);
            return Task.FromResult(created);
        }

        public Task<GradingEvidencePackage> CreateNextVersionAsync(GradingPackageIdentity identity, Guid? batchJobId, CancellationToken cancellationToken) =>
            Task.FromResult(Packages.Single());

        public Task AddMemberAsync(GradingEvidencePackageMember member, CancellationToken cancellationToken)
        {
            Members.Add(member);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GradingEvidencePackageMember>> ListMembersAsync(Guid packageId, CancellationToken cancellationToken)
        {
            ListMembersCalls++;
            return Task.FromResult<IReadOnlyList<GradingEvidencePackageMember>>(Members.Where(item => item.PackageId == packageId).ToArray());
        }

        public Task AddFileAsync(GradingEvidencePackageFile file, CancellationToken cancellationToken)
        {
            Files.Add(file);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GradingEvidencePackageFile>> ListFilesAsync(Guid packageId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GradingEvidencePackageFile>>(Files.Where(item => item.PackageId == packageId).ToArray());

        public Task<bool> TryClaimAsync(Guid packageId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            ClaimedWorkerIds.Add(workerId);
            if (RejectClaims)
            {
                return Task.FromResult(false);
            }

            WasClaimed = Packages.Single(item => item.Id == packageId)
                .TryAcquireLease(workerId, now, leaseDuration);
            return Task.FromResult(WasClaimed);
        }

        public Task<bool> RenewClaimAsync(Guid packageId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            RenewCalls++;
            return Task.FromResult(
                Packages.Single(item => item.Id == packageId)
                    .RenewLease(workerId, now, leaseDuration));
        }

        public Task<bool> ReleaseClaimAsync(Guid packageId, string workerId, DateTimeOffset now, string? errorCode, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken) =>
            Task.FromResult(
                Packages.Single(item => item.Id == packageId)
                    .ReleaseLease(workerId, now, errorCode, nextAttemptAt));

        public Task<int> RecoverExpiredClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<GradingEvidencePackage>> ListExpiredBlobsAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidencePackage>>([]);
        public Task<IReadOnlyList<GradingEvidencePackage>> ListDueAsync(DateTimeOffset now, int maxPackages, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidencePackage>>([]);
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (ThrowAfterFinalSave && !finalSaveThrown && Packages.Any(package =>
                    package.Status is GradingEvidencePackageStatus.Ready or
                    GradingEvidencePackageStatus.PartiallyReady or
                    GradingEvidencePackageStatus.ResultsReceived))
            {
                finalSaveThrown = true;
                return Task.FromException(new IOException("resultado da persistencia final desconhecido"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeBlobStore : IGradingPackageBlobStore
    {
        public Action? AfterWrite { get; init; }
        public bool ThrowOnWrite { get; init; }
        public GradingPackageBlobDescriptor? LastWrite { get; private set; }
        public List<string> DeletedKeys { get; } = [];

        public async Task<GradingPackageBlobDescriptor> WriteAsync(
            string blobKey,
            Func<Stream, CancellationToken, Task> writer,
            CancellationToken cancellationToken)
        {
            if (ThrowOnWrite)
            {
                throw new IOException("storage temporariamente indisponivel");
            }

            LastWrite = await WriteCoreAsync(blobKey, writer, cancellationToken);
            AfterWrite?.Invoke();
            return LastWrite;
        }

        private static async Task<GradingPackageBlobDescriptor> WriteCoreAsync(string blobKey, Func<Stream, CancellationToken, Task> writer, CancellationToken cancellationToken)
        {
            await using var stream = new MemoryStream();
            await writer(stream, cancellationToken);
            var bytes = stream.ToArray();
            return new GradingPackageBlobDescriptor(
                blobKey,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        public Task<Stream> OpenReadAsync(string blobKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));

        public Task DeleteAsync(string blobKey, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(blobKey);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingArchiveWriter : IGradingPackageArchiveWriter
    {
        public async Task<GradingPackageArchiveResult> WriteAsync(Stream destination, IReadOnlyCollection<GradingPackageArchiveEntry> entries, GradingPackageArchiveLimits limits, CancellationToken cancellationToken)
        {
            var bytes = 0L;
            foreach (var entry in entries)
            {
                await entry.WriteContentAsync(destination, cancellationToken);
                bytes += entry.DeclaredSizeBytes ?? 0;
            }

            return new GradingPackageArchiveResult(entries.Count, bytes);
        }
    }

    private sealed class TransientSubmissionFileGateway : IMoodleSubmissionFileGateway
    {
        public Task<SubmissionFileDownloadResult> DownloadFileAsync(string userExternalId, string fileUrl, string filename, long maxBytes, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Moodle temporariamente indisponivel.");
    }

    private sealed class SyntheticFileDownloadGateway(byte[] content, string sha256) : IMoodleSubmissionFileGateway
    {
        private int downloadCount;
        public int DownloadCount => Volatile.Read(ref downloadCount);

        public Task<SubmissionFileDownloadResult> DownloadFileAsync(
            string userExternalId,
            string fileUrl,
            string filename,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref downloadCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SubmissionFileDownloadResult(
                filename,
                "text/plain",
                content.LongLength,
                sha256,
                content,
                false));
        }
    }

    private sealed class SyntheticAssignmentSubmissionsGateway(
        IReadOnlyList<AssignmentSubmissionRecord> records) : IMoodleAssignmentSubmissionsGateway
    {
        public int BatchReadCalls { get; private set; }

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchReadCalls++;
            IReadOnlyList<AssignmentSubmissionsBatch> result = assignmentIds
                .Select(assignmentId => new AssignmentSubmissionsBatch(assignmentId, records))
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssignmentSubmissionRecord>>(records);
    }

    private sealed class ConcurrentSubmissionFileGateway(byte[] content, string sha256) : IMoodleSubmissionFileGateway
    {
        private int active;
        private int maxConcurrency;

        public int MaxConcurrency => Volatile.Read(ref maxConcurrency);

        public async Task<SubmissionFileDownloadResult> DownloadFileAsync(
            string userExternalId,
            string fileUrl,
            string filename,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(current);
            try
            {
                await Task.Delay(40, cancellationToken);
                return new SubmissionFileDownloadResult(
                    filename,
                    "text/plain",
                    content.LongLength,
                    sha256,
                    content,
                    false);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (current > Volatile.Read(ref maxConcurrency))
            {
                var previous = Volatile.Read(ref maxConcurrency);
                if (Interlocked.CompareExchange(ref maxConcurrency, current, previous) == previous)
                {
                    return;
                }
            }
        }
    }

    private sealed class FakeSubmissionsGateway(FakePackageStore packageStore) : IMoodleAssignmentSubmissionsGateway
    {
        public bool SawClaimedPackage { get; private set; }
        public bool ThrowTransientBatchRead { get; init; }
        public Exception? BatchReadFailure { get; init; }
        public int BatchReadCalls { get; private set; }

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            SawClaimedPackage = packageStore.WasClaimed;
            BatchReadCalls++;
            if (BatchReadFailure is not null)
            {
                throw BatchReadFailure;
            }
            if (ThrowTransientBatchRead)
            {
                throw new HttpRequestException("Moodle temporariamente indisponivel.");
            }

            var batches = assignmentIds
                .Select(assignmentId => new AssignmentSubmissionsBatch(
                    assignmentId,
                    [new AssignmentSubmissionRecord(
                        "9001",
                        "321",
                        "submitted",
                        null,
                        null,
                        DateTimeOffset.UtcNow,
                        1,
                        1,
                        false)]))
                .ToArray();
            return Task.FromResult<IReadOnlyList<AssignmentSubmissionsBatch>>(batches);
        }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(string userExternalId, string assignmentId, string? status, DateTimeOffset? since, DateTimeOffset? before, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssignmentSubmissionRecord>>([]);
    }

    private sealed class AllOnlineTextSubmissionsGateway(
        string onlineText = "Resposta sintetica",
        AssignmentSubmissionFile? sourceFile = null) : IMoodleAssignmentSubmissionsGateway
    {
        public int BatchReadCalls { get; private set; }

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            BatchReadCalls++;
            return Task.FromResult<IReadOnlyList<AssignmentSubmissionsBatch>>(
                assignmentIds
                    .Select(assignmentId => new AssignmentSubmissionsBatch(
                        assignmentId,
                        [
                            new AssignmentSubmissionRecord(
                                "9001",
                                "101",
                                "submitted",
                                null,
                                null,
                                DateTimeOffset.UtcNow,
                                1,
                                0,
                                true,
                                Files: sourceFile is null ? [] : [sourceFile],
                                OnlineText: onlineText),
                            new AssignmentSubmissionRecord(
                                "9002",
                                "102",
                                "submitted",
                                null,
                                null,
                                DateTimeOffset.UtcNow,
                                1,
                                0,
                                true,
                                Files: sourceFile is null ? [] : [sourceFile],
                                OnlineText: onlineText)
                        ]))
                    .ToArray());
        }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssignmentSubmissionRecord>>([]);
    }

    private sealed class RetryAwareSubmissionFileGateway(
        IReadOnlyDictionary<string, byte[]> contents,
        string failOnceUrl) : IMoodleSubmissionFileGateway
    {
        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        public Task<SubmissionFileDownloadResult> DownloadFileAsync(
            string userExternalId,
            string fileUrl,
            string filename,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            Calls[fileUrl] = Calls.GetValueOrDefault(fileUrl) + 1;
            if (string.Equals(fileUrl, failOnceUrl, StringComparison.Ordinal) && Calls[fileUrl] == 1)
            {
                throw new HttpRequestException("Moodle temporariamente indisponivel.");
            }

            var content = contents[fileUrl];
            return Task.FromResult(new SubmissionFileDownloadResult(
                filename,
                "application/pdf",
                content.LongLength,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                content,
                false));
        }
    }

    private sealed class InMemoryArtifactCache : IGradingPackageArtifactCache
    {
        private readonly Dictionary<(Guid ArtifactId, string Sha256), byte[]> entries = [];

        public int HitCount { get; private set; }

        public Task<GradingPackageArtifactCacheHit?> TryOpenReadAsync(
            Guid artifactId,
            string sha256,
            long? expectedSizeBytes,
            CancellationToken cancellationToken)
        {
            if (!entries.TryGetValue((artifactId, sha256), out var bytes))
            {
                return Task.FromResult<GradingPackageArtifactCacheHit?>(null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, sha256, StringComparison.OrdinalIgnoreCase) ||
                expectedSizeBytes is > 0 && expectedSizeBytes != bytes.LongLength)
            {
                entries.Remove((artifactId, sha256));
                return Task.FromResult<GradingPackageArtifactCacheHit?>(null);
            }

            HitCount++;
            return Task.FromResult<GradingPackageArtifactCacheHit?>(new GradingPackageArtifactCacheHit(
                new MemoryStream(bytes, writable: false),
                bytes.LongLength,
                actualHash));
        }

        public async Task StoreAsync(
            Guid artifactId,
            string sha256,
            long sizeBytes,
            Func<Stream, CancellationToken, Task> writer,
            CancellationToken cancellationToken)
        {
            await using var output = new MemoryStream();
            await writer(output, cancellationToken);
            var bytes = output.ToArray();
            Assert.Equal(sizeBytes, bytes.LongLength);
            Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            entries[(artifactId, sha256)] = bytes;
        }

        public Task<int> RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class ThrowingArtifactCache : IGradingPackageArtifactCache
    {
        public Task<GradingPackageArtifactCacheHit?> TryOpenReadAsync(
            Guid artifactId,
            string sha256,
            long? expectedSizeBytes,
            CancellationToken cancellationToken) =>
            Task.FromException<GradingPackageArtifactCacheHit?>(
                new IOException("cache temporariamente indisponivel"));

        public Task StoreAsync(
            Guid artifactId,
            string sha256,
            long sizeBytes,
            Func<Stream, CancellationToken, Task> writer,
            CancellationToken cancellationToken) =>
            Task.FromException(new IOException("cache temporariamente indisponivel"));

        public Task<int> RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class FakeReviewRepository : IGradingReviewRepository
    {
        public List<AssistedGradingBatch> Batches { get; } = [];
        public List<AssistedGradingItem> Items { get; } = [];
        public List<GradingArtifact> Artifacts { get; } = [];
        public Dictionary<Guid, GradingContextSnapshotDocument> ContextSnapshots { get; } = [];
        public int ListItemsByBatchCalls { get; private set; }
        public int GetBatchesAsyncCalls { get; private set; }
        public IReadOnlyCollection<Guid> LastBatchIds { get; private set; } = [];

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken) { Batches.Add(batch); return Task.CompletedTask; }
        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Batches.SingleOrDefault(item => item.Id == id));
        public Task<IReadOnlyDictionary<Guid, AssistedGradingBatch>> GetBatchesAsync(
            IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken)
        {
            GetBatchesAsyncCalls++;
            LastBatchIds = ids.Distinct().ToArray();
            IReadOnlyDictionary<Guid, AssistedGradingBatch> batchesById = Batches
                .Where(item => LastBatchIds.Contains(item.Id))
                .ToDictionary(item => item.Id);
            return Task.FromResult(batchesById);
        }
        public Task AddItemAsync(AssistedGradingItem item, CancellationToken cancellationToken) { Items.Add(item); return Task.CompletedTask; }
        public Task AddArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken) { Artifacts.Add(artifact); return Task.CompletedTask; }
        public Task AddEvidenceAsync(GradingEvidence evidence, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AssistedGradingItem?> GetItemAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<AssistedGradingItem>> ListItemsByBatchAsync(Guid batchId, int page, int pageSize, CancellationToken cancellationToken)
        {
            ListItemsByBatchCalls++;
            return Task.FromResult<IReadOnlyList<AssistedGradingItem>>(Items.Where(item => item.BatchId == batchId).Skip((page - 1) * pageSize).Take(pageSize).ToArray());
        }
        public Task<int> CountItemsByBatchAsync(Guid batchId, CancellationToken cancellationToken) => Task.FromResult(Items.Count(item => item.BatchId == batchId));
        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(Guid gradingItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingArtifact>>(Artifacts.Where(item => item.GradingItemId == gradingItemId).ToArray());
        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(Guid gradingItemId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidence>>([]);
        public Task<IReadOnlyDictionary<Guid, GradingContextSnapshotDocument>> ListLatestContextSnapshotsByItemsAsync(IReadOnlyCollection<Guid> gradingItemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, GradingContextSnapshotDocument>>(
                ContextSnapshots.Where(pair => gradingItemIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(GradingBatchStatus status, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>([]);
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>([]);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class CapturingAdmissionWorkScope : IMoodleAdmissionWorkScope
    {
        public MoodleAdmissionOrigin? LastOrigin { get; private set; }
        public string? LastOwnerId { get; private set; }
        public MoodleAdmissionPriority? LastPriority { get; private set; }

        public IDisposable Begin(
            MoodleAdmissionOrigin origin,
            string ownerId,
            MoodleAdmissionPriority priority,
            DateTimeOffset? deadlineUtc = null)
        {
            LastOrigin = origin;
            LastOwnerId = ownerId;
            LastPriority = priority;
            return NoopDisposable.Instance;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class CapturingGradingTelemetry : IGradingOperationTelemetry
    {
        public List<(string CacheResult, int SubmissionCount, long EstimatedBytes, int GatewayCallCount)> SubmissionReads { get; } = [];
        public List<(string Status, int AttemptCount, string? RetryReason)> Outcomes { get; } = [];

        public void RecordPhase(
            string operation,
            string phase,
            string result,
            double durationMs,
            int queryCount = 0,
            int itemCount = 0,
            long bytes = 0)
        {
        }

        public void RecordPackageSubmissionRead(
            string cacheResult,
            double durationMs,
            int submissionCount,
            long estimatedBytes,
            int gatewayCallCount) =>
            SubmissionReads.Add((cacheResult, submissionCount, estimatedBytes, gatewayCallCount));

        public void RecordPackageOutcome(
            string status,
            int attemptCount,
            string? retryReason,
            int artifactReadFailedCount,
            int contextUnavailableCount) =>
            Outcomes.Add((status, attemptCount, retryReason));
    }
}
