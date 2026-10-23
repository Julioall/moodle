using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Application.Grading.Packages;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPackageActivityPreparationTests
{
    [Fact]
    public async Task Default_mode_combines_sibling_batches_and_persists_members_with_a_child_anchor()
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildAsync();
        var second = await fixture.AddChildAsync();

        var result = await fixture.Handler.Handle(new PrepareGradingPackagesCommand(fixture.Run.Id), default);

        var descriptor = Assert.Single(result.Packages);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups));
        Assert.Empty(result.ReusedPackageIds);
        Assert.Equal(fixture.Run.Id, result.BatchJobId);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), result.PendingItems.Order());
        await fixture.AssertPersistedCoverageAsync(result, [first, second]);
        var package = await fixture.Store.GetAsync(descriptor.PackageId, default);
        Assert.NotNull(package);
        Assert.Contains(package.BatchJobId, new Guid?[] { first.BatchId, second.BatchId });
        Assert.NotEqual(fixture.Run.Id, package.BatchJobId);
        Assert.True(fixture.Channel.TryRead(out var work));
        Assert.Equal(package.Id, work.PackageId);
        Assert.Equal(package.BatchJobId, work.BatchJobId);
        Assert.False(fixture.Channel.TryRead(out _));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("course")]
    [InlineData("connection")]
    [InlineData("assignment")]
    [InlineData("owner")]
    [InlineData("teacherInstructions")]
    public async Task Per_activity_keeps_incompatible_siblings_in_separate_packages(string difference)
    {
        // Admin access makes the owner case exercise grouping, rather than fail authorization.
        await using var fixture = new Fixture(admin: true);
        var first = await fixture.AddChildAsync();
        var second = await fixture.AddChildAsync(
            contextHash: difference == "context" ? new string('b', 64) : new string('a', 64),
            courseId: difference == "course" ? 20 : 10,
            connectionId: difference == "connection" ? "connection-2" : "connection-1",
            assignmentId: difference == "assignment" ? 502 : 501,
            owner: difference == "owner" ? "teacher-2" : "teacher-1",
            instructions: difference == "teacherInstructions" ? "Use the revised rubric." : "Use the rubric.");

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Equal(2, result.Packages.Count);
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups).Count);
        await fixture.AssertPersistedCoverageAsync(result, [first, second]);
        foreach (var descriptor in result.Packages)
        {
            var member = Assert.Single(await fixture.Store.ListMembersAsync(descriptor.PackageId, default));
            var item = new[] { first, second }.Single(value => value.Id == member.GradingItemId);
            var package = await fixture.Store.GetAsync(descriptor.PackageId, default);
            Assert.NotNull(package);
            Assert.Equal(item.BatchId, package.BatchJobId);
            Assert.Equal(item.CourseId, package.CourseId);
            Assert.Equal(item.AssignmentId, package.AssignmentId);
        }
    }

    [Fact]
    public async Task Repeating_preparation_reuses_the_cross_batch_package_without_duplicate_members()
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildAsync();
        var second = await fixture.AddChildAsync();
        var initial = await fixture.PrepareAsync("per_activity");
        var packageId = Assert.Single(initial.Packages).PackageId;
        fixture.Db.ChangeTracker.Clear();
        fixture.PackageStoreRecorder.Reset();

        var repeated = await fixture.PrepareAsync("per_activity");

        Assert.Equal(packageId, Assert.Single(repeated.Packages).PackageId);
        Assert.Equal(packageId, Assert.Single(repeated.ReusedPackageIds));
        Assert.Equal(1, fixture.PackageStoreRecorder.AcquireCalls);
        Assert.Equal(0, fixture.PackageStoreRecorder.FindReusableCalls);
        Assert.Equal(1, fixture.PackageStoreRecorder.ListMembersCalls);
        Assert.Equal(1, await fixture.Db.GradingEvidencePackages.CountAsync());
        await fixture.AssertPersistedCoverageAsync(repeated, [first, second]);
    }

    [Fact]
    public async Task New_package_uses_one_atomic_store_acquisition()
    {
        await using var fixture = new Fixture();
        await fixture.AddChildAsync();

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Single(result.Packages);
        Assert.Empty(result.ReusedPackageIds);
        Assert.Equal(1, fixture.PackageStoreRecorder.AcquireCalls);
        Assert.Equal(0, fixture.PackageStoreRecorder.FindReusableCalls);
        Assert.Equal(0, fixture.PackageStoreRecorder.GetOrCreateCalls);
        Assert.Equal(1, fixture.PackageStoreRecorder.ListMembersCalls);
    }

    [Fact]
    public async Task Regenerate_transport_invalidates_shared_submission_read()
    {
        using var cache = new GradingPackageSubmissionReadCache(
            Options.Create(new GradingLimitsOptions { GradingPackageSubmissionReuseEnabled = true }),
            TimeProvider.System);
        await using var fixture = new Fixture(submissionReadCache: cache);
        var item = await fixture.AddChildAsync();
        var initial = await fixture.PrepareAsync("per_activity");
        var package = await fixture.Store.GetAsync(Assert.Single(initial.Packages).PackageId, default);
        Assert.NotNull(package);
        var now = DateTimeOffset.UtcNow;
        package!.MarkManifest("{}", new string('a', 64), now);
        package.MarkReady("packages/initial.zip", new string('b', 64), 100, 120, 2, 1, 1, 0, now);
        await fixture.Store.SaveChangesAsync(default);

        var readScope = new GradingPackageSubmissionReadScope(
            fixture.Run.Id, true, "teacher-1", 321, "connection-1", "client-1", "alias-1", 10, 501);
        var reads = 0;
        Task<GradingPackageSubmissionReadSnapshot> Load()
        {
            reads++;
            return Task.FromResult(new GradingPackageSubmissionReadSnapshot(
                new Dictionary<(long, string), Moodle.Domain.AssignmentSubmissionRecord>
                {
                    [(501, item.SubmissionId!.Value.ToString())] = new(
                        item.SubmissionId.Value.ToString(), item.MoodleUserId.ToString(), "submitted", null,
                        null, null, 1, 0, false)
                },
                IsComplete: true));
        }

        await cache.GetOrLoadAsync(readScope, 1, 1, [(501, item.SubmissionId!.Value.ToString())],
            _ => Load(), default);
        var regenerated = await fixture.Handler.Handle(
            new PrepareGradingPackagesCommand(fixture.Run.Id, RegenerateTransport: true), default);
        Assert.Contains(initial.Packages[0].PackageId, regenerated.Packages.Select(descriptor => descriptor.PackageId));
        Assert.Contains(regenerated.Packages, descriptor => descriptor.PackageId != initial.Packages[0].PackageId);

        var afterRegeneration = await cache.GetOrLoadAsync(
            readScope, 2, 1, [(501, item.SubmissionId!.Value.ToString())], _ => Load(), default);
        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Miss, afterRegeneration.CacheStatus);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Per_submission_creates_singletons_even_for_compatible_items_in_the_same_child()
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildAsync(itemCount: 2);
        var sibling = await fixture.AddChildAsync();
        var items = fixture.Items.ToArray();

        var result = await fixture.PrepareAsync("per_submission");

        Assert.Equal(3, result.Packages.Count);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups));
        await fixture.AssertPersistedCoverageAsync(result, items);
        foreach (var descriptor in result.Packages)
        {
            var member = Assert.Single(await fixture.Store.ListMembersAsync(descriptor.PackageId, default));
            var item = items.Single(value => value.Id == member.GradingItemId);
            var package = await fixture.Store.GetAsync(descriptor.PackageId, default);
            Assert.NotNull(package);
            Assert.Equal(item.BatchId, package.BatchJobId);
        }
        Assert.NotEqual(first.BatchId, sibling.BatchId);
    }

    [Fact]
    public async Task Per_activity_splits_across_child_boundaries_at_MaxItems_without_losing_members()
    {
        await using var fixture = new Fixture(maxItems: 2);
        // Five one-item children require three activity packages, not five child packages.
        for (var index = 0; index < 5; index++)
        {
            await fixture.AddChildAsync();
        }

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Equal(3, result.Packages.Count);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups));
        await fixture.AssertPersistedCoverageAsync(result, fixture.Items);
        var sizes = new List<int>();
        foreach (var descriptor in result.Packages)
        {
            sizes.Add((await fixture.Store.ListMembersAsync(descriptor.PackageId, default)).Count);
        }
        Assert.Equal(new[] { 1, 2, 2 }, sizes.Order());

        fixture.Db.ChangeTracker.Clear();
        var repeated = await fixture.PrepareAsync("per_activity");
        Assert.Equal(result.Packages.Select(package => package.PackageId).Order(), repeated.ReusedPackageIds.Order());
        Assert.Equal(3, await fixture.Db.GradingEvidencePackages.CountAsync());
        await fixture.AssertPersistedCoverageAsync(repeated, fixture.Items);
    }

    [Fact]
    public async Task Per_activity_partitions_18_16_17_items_into_three_complete_activity_packages()
    {
        await using var fixture = new Fixture(maxItems: 20);
        var expectedCounts = new Dictionary<long, int>
        {
            [501] = 18,
            [502] = 16,
            [503] = 17
        };
        foreach (var (assignmentId, itemCount) in expectedCounts)
        {
            for (var index = 0; index < itemCount; index++)
            {
                await fixture.AddChildAsync(assignmentId: assignmentId);
            }
        }

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Equal(3, result.Packages.Count);
        var groups = Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups);
        Assert.Equal(3, groups.Count);
        Assert.Equal(expectedCounts.Keys.Order(), groups.Select(group => group.AssignmentId).Order());
        Assert.Equal(expectedCounts.Values.Sum(), groups.Sum(group => group.SelectedItems));
        Assert.All(groups, group => Assert.Single(group.Parts));
        Assert.Equal(expectedCounts.Values.Sum(), groups
            .SelectMany(group => group.Parts)
            .SelectMany(part => part.GradingItemIds)
            .Distinct()
            .Count());
        Assert.Equal(expectedCounts.Values.Sum(), fixture.Items.Count);
        await fixture.AssertPersistedCoverageAsync(result, fixture.Items);

        foreach (var group in groups)
        {
            var part = Assert.Single(group.Parts);
            Assert.Contains(result.Packages, package => package.PackageId == part.PackageId);
            Assert.Equal(expectedCounts[group.AssignmentId], group.SelectedItems);
            var members = await fixture.Store.ListMembersAsync(part.PackageId, default);
            Assert.Equal(expectedCounts[group.AssignmentId], members.Count);
            Assert.Equal(group.AssignmentId, (await fixture.Store.GetAsync(part.PackageId, default))!.AssignmentId);
        }
    }

    [Fact]
    public async Task Run_preparation_does_not_include_compatible_items_from_another_run()
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildAsync();
        var second = await fixture.AddChildAsync();
        var otherRun = GradingRun.Create("teacher-1");
        await fixture.Repository.AddGradingRunAsync(otherRun, default);
        var unrelated = await fixture.AddChildAsync(runId: otherRun.Id);

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Single(result.Packages);
        await fixture.AssertPersistedCoverageAsync(result, [first, second]);
        Assert.DoesNotContain(unrelated.Id, result.PendingItems);
    }

    [Fact]
    public async Task Direct_child_request_keeps_default_per_activity_within_that_child()
    {
        await using var fixture = new Fixture();
        var selected = await fixture.AddChildAsync(itemCount: 2);
        var selectedItems = fixture.Items.ToArray();
        var sibling = await fixture.AddChildAsync();

        var result = await fixture.Handler.Handle(new PrepareGradingPackagesCommand(selected.BatchId), default);

        Assert.Equal(selected.BatchId, result.BatchJobId);
        Assert.Single(result.Packages);
        await fixture.AssertPersistedCoverageAsync(result, selectedItems);
        Assert.Equal(selectedItems.Select(item => item.Id).Order(), result.PendingItems.Order());
        Assert.DoesNotContain(sibling.Id, result.PendingItems);
        Assert.True(fixture.Channel.TryRead(out var work));
        Assert.Equal(selected.BatchId, work.BatchJobId);
        Assert.Equal(result.Packages[0].PackageId, work.PackageId);
        Assert.False(fixture.Channel.TryRead(out _));
    }

    [Fact]
    public async Task DraftReady_is_excluded_by_default_and_preserved_when_regenerating_transport()
    {
        await using var fixture = new Fixture();
        var awaiting = await fixture.AddChildAsync();
        var draft = await fixture.AddChildAsync();
        draft.SetDraft(8m, .9m, "Existing reviewed feedback.", "Keep this private note.", maxGrade: 10m);
        await fixture.Repository.SaveChangesAsync(default);
        var draftUpdatedAt = draft.UpdatedAt;
        fixture.Db.ChangeTracker.Clear();

        var initial = await fixture.Handler.Handle(new PrepareGradingPackagesCommand(fixture.Run.Id), default);

        Assert.Single(initial.Packages);
        await fixture.AssertPersistedCoverageAsync(initial, [awaiting]);
        Assert.DoesNotContain(draft.Id, initial.PendingItems);
        await AssertDraftPreservedAsync();

        var regenerated = await fixture.Handler.Handle(
            new PrepareGradingPackagesCommand(fixture.Run.Id, RegenerateTransport: true), default);

        Assert.Single(regenerated.Packages);
        await fixture.AssertPersistedCoverageAsync(regenerated, [awaiting, draft]);
        await AssertDraftPreservedAsync();

        async Task AssertDraftPreservedAsync()
        {
            fixture.Db.ChangeTracker.Clear();
            var persisted = await fixture.Repository.GetItemAsync(draft.Id, default);
            Assert.NotNull(persisted);
            Assert.Equal(GradingItemStatus.DraftReady, persisted.Status);
            Assert.Equal(8m, persisted.SuggestedGrade);
            Assert.Equal(.9m, persisted.Confidence);
            Assert.Equal("Existing reviewed feedback.", persisted.DraftFeedback);
            Assert.Equal("Keep this private note.", persisted.PrivateNotesToTeacher);
            Assert.Equal(draft.ContextHash, persisted.ContextHash);
            Assert.Equal(draft.SubmissionContentHash, persisted.SubmissionContentHash);
            Assert.Equal(draftUpdatedAt, persisted.UpdatedAt);
        }
    }

    [Fact]
    public async Task Preparation_loads_artifacts_only_for_transport_eligible_items()
    {
        await using var fixture = new Fixture(recordArtifactLookups: true);
        var awaiting = await fixture.AddChildAsync();
        var draft = await fixture.AddChildAsync();
        draft.SetDraft(8m, .9m, "Reviewed feedback.", "Teacher note.", maxGrade: 10m);
        await fixture.Repository.SaveChangesAsync(default);

        await fixture.Handler.Handle(new PrepareGradingPackagesCommand(fixture.Run.Id), default);

        var recorder = Assert.IsAssignableFrom<RecordingGradingReviewRepositoryProxy>(fixture.ArtifactLookupRecorder);
        Assert.Equal(new[] { awaiting.Id }, Assert.Single(recorder.ArtifactQueries));

        recorder.ArtifactQueries.Clear();
        await fixture.Handler.Handle(
            new PrepareGradingPackagesCommand(fixture.Run.Id, RegenerateTransport: true), default);

        Assert.Equal(new[] { awaiting.Id, draft.Id }.Order(), Assert.Single(recorder.ArtifactQueries).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_child_does_not_reprocess_stale_awaiting_items(bool requestRun)
    {
        await using var fixture = new Fixture();
        var stale = await fixture.AddChildAsync();
        var batch = await fixture.Repository.GetBatchAsync(stale.BatchId, default);
        Assert.NotNull(batch);
        // Cancel() changes the batch and lease, leaving item statuses untouched.
        batch.Cancel();
        await fixture.Repository.SaveChangesAsync(default);
        var active = await fixture.AddChildAsync();
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(GradingBatchStatus.Cancelled,
            (await fixture.Repository.GetBatchAsync(stale.BatchId, default))!.Status);
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis,
            (await fixture.Repository.GetItemAsync(stale.Id, default))!.Status);

        var result = await fixture.Handler.Handle(
            new PrepareGradingPackagesCommand(requestRun ? fixture.Run.Id : stale.BatchId), default);

        if (requestRun)
        {
            Assert.Single(result.Packages);
            await fixture.AssertPersistedCoverageAsync(result, [active]);
            Assert.True(fixture.Channel.TryRead(out var work));
            Assert.Equal(active.BatchId, work.BatchJobId);
        }
        else
        {
            Assert.Empty(result.Packages);
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups));
            Assert.Equal(0, await fixture.Db.GradingEvidencePackages.CountAsync());
        }
        Assert.False(fixture.Channel.TryRead(out _));
        Assert.DoesNotContain(stale.Id, result.PendingItems);
        Assert.False(await fixture.Db.GradingEvidencePackageMembers.AnyAsync(member => member.GradingItemId == stale.Id));
    }

    [Fact]
    public async Task Explicit_single_item_selection_in_a_run_does_not_expand_to_compatible_items()
    {
        await using var fixture = new Fixture();
        await fixture.AddChildAsync();
        var selected = await fixture.AddChildAsync(itemCount: 2);

        var result = await fixture.Handler.Handle(
            new PrepareGradingPackagesCommand(fixture.Run.Id, GradingItemIds: [selected.Id]), default);

        Assert.Single(result.Packages);
        await fixture.AssertPersistedCoverageAsync(result, [selected]);
        Assert.Equal(selected.Id, Assert.Single(result.PendingItems));
        Assert.True(fixture.Channel.TryRead(out var work));
        Assert.Equal(selected.BatchId, work.BatchJobId);
        Assert.Equal(result.Packages[0].PackageId, work.PackageId);
        Assert.False(fixture.Channel.TryRead(out _));

        var repeated = await fixture.Handler.Handle(
            new PrepareGradingPackagesCommand(fixture.Run.Id, GradingItemIds: [selected.Id]), default);
        Assert.Equal(result.Packages[0].PackageId, Assert.Single(repeated.ReusedPackageIds));
        await fixture.AssertPersistedCoverageAsync(repeated, [selected]);
        Assert.Equal(1, await fixture.Db.GradingEvidencePackages.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_selection_rejects_outside_scope_ids_before_creating_any_packages(bool requestRun)
    {
        await using var fixture = new Fixture();
        var selected = await fixture.AddChildAsync();
        Guid? otherRunId = null;
        if (requestRun)
        {
            var otherRun = GradingRun.Create("teacher-1");
            await fixture.Repository.AddGradingRunAsync(otherRun, default);
            otherRunId = otherRun.Id;
        }
        // A sibling is outside a direct-child scope; another run is outside a run scope.
        var outside = await fixture.AddChildAsync(runId: otherRunId);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Handler.Handle(
            new PrepareGradingPackagesCommand(
                requestRun ? fixture.Run.Id : selected.BatchId,
                GradingItemIds: [selected.Id, outside.Id]), default));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(0, await fixture.Db.GradingEvidencePackages.CountAsync());
        Assert.Equal(0, await fixture.Db.GradingEvidencePackageMembers.CountAsync());
        Assert.False(fixture.Channel.TryRead(out _));
    }

    [Fact]
    public async Task Real_student_snapshots_with_different_item_hashes_share_one_activity_package()
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildWithSnapshotAsync(101, "The solution uses iteration.");
        var second = await fixture.AddChildWithSnapshotAsync(102, "I chose recursion and explained its termination.");
        Assert.NotEqual(first.BatchId, second.BatchId);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.MoodleUserId, second.MoodleUserId);
        Assert.NotEqual(first.ContextHash, second.ContextHash);
        fixture.Db.ChangeTracker.Clear();
        var snapshots = await fixture.Repository.ListLatestContextSnapshotsByItemsAsync([first.Id, second.Id], default);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(first.ContextHash, snapshots[first.Id].ContextHash);
        Assert.Equal(second.ContextHash, snapshots[second.Id].ContextHash);

        var result = await fixture.Handler.Handle(new PrepareGradingPackagesCommand(fixture.Run.Id), default);

        var package = Assert.Single(result.Packages);
        var group = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups));
        Assert.False(string.IsNullOrWhiteSpace(group.ContextHash));
        Assert.Equal(2, group.SelectedItems);
        var part = Assert.Single(group.Parts);
        Assert.Equal(package.PackageId, part.PackageId);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), part.GradingItemIds.Order());
        fixture.Db.ChangeTracker.Clear();
        var members = await fixture.Store.ListMembersAsync(package.PackageId, default);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), members.Select(member => member.GradingItemId).Order());
        // Transport grouping must not replace the student-specific integrity identities.
        Assert.Equal(first.ContextHash, members.Single(member => member.GradingItemId == first.Id).ContextHash);
        Assert.Equal(second.ContextHash, members.Single(member => member.GradingItemId == second.Id).ContextHash);
    }

    [Fact]
    public async Task Per_activity_ignores_artifact_ids_and_source_metadata_when_content_hashes_match()
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactContent: "same assignment instructions bytes",
            contextSource: new GradingSourceMetadata("resource", 701, 1, "Original file source", 0));
        var second = await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactContent: "same assignment instructions bytes",
            contextSource: new GradingSourceMetadata("resource", 9902, 4, "Materialized copy", 3));
        Assert.NotEqual(first.ContextHash, second.ContextHash);

        var result = await fixture.Handler.Handle(new PrepareGradingPackagesCommand(fixture.Run.Id), default);

        var package = Assert.Single(result.Packages);
        var group = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups));
        Assert.Equal(2, group.SelectedItems);
        Assert.NotEqual(first.ContextHash, group.ContextHash);
        Assert.NotEqual(second.ContextHash, group.ContextHash);
        var part = Assert.Single(group.Parts);
        Assert.Equal(package.PackageId, part.PackageId);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), part.GradingItemIds.Order());
        fixture.Db.ChangeTracker.Clear();
        var members = await fixture.Store.ListMembersAsync(package.PackageId, default);
        Assert.Equal(first.ContextHash, members.Single(member => member.GradingItemId == first.Id).ContextHash);
        Assert.Equal(second.ContextHash, members.Single(member => member.GradingItemId == second.Id).ContextHash);
    }

    [Fact]
    public async Task Missing_context_hash_is_materialized_once_and_sibling_deliveries_share_activity_package()
    {
        var url = "https://moodle.example/context/assignment.pdf";
        var content = System.Text.Encoding.UTF8.GetBytes("identical shared assignment instructions");
        var gateway = new RecordingSubmissionFileGateway(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [url] = content
        });
        var cache = new RecordingArtifactCache();
        await using var fixture = new Fixture(submissionFileGateway: gateway, artifactCache: cache);
        var first = await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: url);
        var second = await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: url);

        var result = await fixture.PrepareAsync("per_activity");

        var package = Assert.Single(result.Packages);
        var group = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups));
        Assert.Equal(2, group.SelectedItems);
        Assert.False(string.IsNullOrWhiteSpace(group.ContextHash));
        Assert.Equal(new[] { first.Id, second.Id }.Order(),
            group.Parts.SelectMany(part => part.GradingItemIds).Order());
        Assert.Equal(1, gateway.Calls[url]);

        fixture.Db.ChangeTracker.Clear();
        var artifactsByItem = await fixture.Repository.ListArtifactsByItemsAsync([first.Id, second.Id], default);
        var contextArtifacts = artifactsByItem.Values.Select(artifacts =>
            Assert.Single(artifacts, artifact => GradingPackageArtifactTypes.IsContext(artifact.ArtifactType))).ToArray();
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        Assert.All(contextArtifacts, artifact => Assert.Equal(expectedHash, artifact.Sha256));
        Assert.Single(cache.StoredEntries);
        Assert.Contains(cache.StoredEntries, entry => entry.Sha256 == expectedHash && entry.Content.SequenceEqual(content));
        var batch = await fixture.Repository.GetBatchAsync(first.BatchId, default);
        Assert.NotNull(batch);
        var cached = await cache.TryOpenReadAsync(
            GradingPackageContextIdentity.GetCacheId(batch, first.AssignmentId, contextArtifacts.Single(artifact => artifact.GradingItemId == first.Id)),
            expectedHash,
            content.LongLength,
            default);
        var cacheHit = Assert.IsType<GradingPackageArtifactCacheHit>(cached);
        await using (cacheHit.Content)
        using (var cachedBytes = new MemoryStream())
        {
            await cacheHit.Content.CopyToAsync(cachedBytes);
            Assert.Equal(content, cachedBytes.ToArray());
        }

        var members = await fixture.Store.ListMembersAsync(package.PackageId, default);
        Assert.Equal(first.ContextHash, members.Single(member => member.GradingItemId == first.Id).ContextHash);
        Assert.Equal(second.ContextHash, members.Single(member => member.GradingItemId == second.Id).ContextHash);
    }

    [Fact]
    public async Task Snapshot_hash_is_verified_and_copied_to_persisted_artifact_before_grouping()
    {
        var url = "https://moodle.example/context/known-hash.pdf";
        var content = System.Text.Encoding.UTF8.GetBytes("shared context with an existing snapshot hash");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var gateway = new RecordingSubmissionFileGateway(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [url] = content
        });
        await using var fixture = new Fixture(submissionFileGateway: gateway, artifactCache: new RecordingArtifactCache());
        var first = await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactContent: "shared context with an existing snapshot hash",
            contextArtifactPersistedSha256Missing: true,
            contextArtifactSourceUrl: url);
        var second = await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactContent: "shared context with an existing snapshot hash",
            contextArtifactPersistedSha256Missing: true,
            contextArtifactSourceUrl: url);

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Single(result.Packages);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups));
        Assert.Equal(1, gateway.Calls[url]);
        var artifacts = await fixture.Repository.ListArtifactsByItemsAsync([first.Id, second.Id], default);
        Assert.All(artifacts.Values.SelectMany(value => value)
            .Where(artifact => GradingPackageArtifactTypes.IsContext(artifact.ArtifactType)),
            artifact => Assert.Equal(hash, artifact.Sha256));
    }

    [Fact]
    public async Task Context_bytes_that_disagree_with_snapshot_hash_are_not_cached_as_verified_content()
    {
        var url = "https://moodle.example/context/tampered.pdf";
        var snapshotContent = System.Text.Encoding.UTF8.GetBytes("the assignment context in the snapshot");
        var downloadedContent = System.Text.Encoding.UTF8.GetBytes("different bytes returned by Moodle");
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snapshotContent)).ToLowerInvariant();
        var gateway = new RecordingSubmissionFileGateway(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [url] = downloadedContent
        });
        var cache = new RecordingArtifactCache();
        await using var fixture = new Fixture(submissionFileGateway: gateway, artifactCache: cache);
        var item = await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactContent: "the assignment context in the snapshot",
            contextArtifactPersistedSha256Missing: true,
            contextArtifactSourceUrl: url);

        await fixture.PrepareAsync("per_activity");

        var artifacts = await fixture.Repository.ListArtifactsByItemAsync(item.Id, default);
        var contextArtifact = Assert.Single(artifacts, artifact => GradingPackageArtifactTypes.IsContext(artifact.ArtifactType));
        Assert.Equal(expectedHash, contextArtifact.Sha256);
        Assert.Empty(cache.StoredEntries);
    }

    [Fact]
    public async Task Failed_context_materialization_keeps_items_isolated_without_failing_preparation()
    {
        var url = "https://moodle.example/context/unavailable.pdf";
        var gateway = new RecordingSubmissionFileGateway(new Dictionary<string, byte[]>(),
            new IOException("simulated context download failure"));
        await using var fixture = new Fixture(submissionFileGateway: gateway);
        await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: url);
        await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: url);

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Equal(2, result.Packages.Count);
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups).Count);
        Assert.All(result.ActivityGroups!, group => Assert.Null(group.ContextHash));
        Assert.Contains(result.Warnings, warning => warning.Contains("context", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Timed_out_context_download_is_treated_as_item_failure_not_request_cancellation()
    {
        var url = "https://moodle.example/context/timeout.pdf";
        var gateway = new RecordingSubmissionFileGateway(new Dictionary<string, byte[]>(),
            new TaskCanceledException("simulated HTTP timeout"));
        await using var fixture = new Fixture(submissionFileGateway: gateway);
        await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: url);
        await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: url);

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Equal(2, result.Packages.Count);
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups).Count);
        Assert.All(result.ActivityGroups!, group => Assert.Null(group.ContextHash));
        Assert.Contains(result.Warnings, warning => warning.Contains("context", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_context_hash_with_different_downloaded_bytes_creates_separate_activity_groups()
    {
        var firstUrl = "https://moodle.example/context/version-one.pdf";
        var secondUrl = "https://moodle.example/context/version-two.pdf";
        var firstContent = System.Text.Encoding.UTF8.GetBytes("assignment instructions version one");
        var secondContent = System.Text.Encoding.UTF8.GetBytes("assignment instructions version two");
        var gateway = new RecordingSubmissionFileGateway(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [firstUrl] = firstContent,
            [secondUrl] = secondContent
        });
        await using var fixture = new Fixture(submissionFileGateway: gateway);
        var first = await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: firstUrl);
        var second = await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: secondUrl);

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Equal(2, result.Packages.Count);
        var groups = Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.False(string.IsNullOrWhiteSpace(group.ContextHash)));
        Assert.Equal(2, groups.Select(group => group.ContextHash).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { first.Id, second.Id }.Order(),
            groups.SelectMany(group => group.Parts).SelectMany(part => part.GradingItemIds).Order());
    }

    [Fact]
    public async Task Per_submission_mode_does_not_download_context_artifacts_for_grouping()
    {
        var url = "https://moodle.example/context/not-needed-for-singletons.pdf";
        var gateway = new RecordingSubmissionFileGateway(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [url] = System.Text.Encoding.UTF8.GetBytes("shared context")
        });
        await using var fixture = new Fixture(submissionFileGateway: gateway);
        await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: url);
        await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactMissingSha256: true,
            contextArtifactSourceUrl: url);

        var result = await fixture.PrepareAsync("per_submission");

        Assert.Equal(2, result.Packages.Count);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task Per_activity_keeps_different_context_artifact_content_in_separate_groups()
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactContent: "assignment instructions version one");
        var second = await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactContent: "assignment instructions version two");

        var result = await fixture.Handler.Handle(new PrepareGradingPackagesCommand(fixture.Run.Id), default);

        Assert.Equal(2, result.Packages.Count);
        var groups = Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.Select(group => group.ContextHash).Distinct().Count());
        Assert.Equal(new[] { first.Id, second.Id }.Order(),
            groups.SelectMany(group => group.Parts).SelectMany(part => part.GradingItemIds).Order());
    }

    [Fact]
    public async Task Per_activity_keeps_distinct_assignment_scales_in_separate_groups()
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildWithSnapshotAsync(
            101,
            "Synthetic response one.",
            maxGrade: 10m);
        var second = await fixture.AddChildWithSnapshotAsync(
            102,
            "Synthetic response two.",
            maxGrade: 20m);

        var result = await fixture.PrepareAsync("per_activity");

        Assert.Equal(2, result.Packages.Count);
        var groups = Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups);
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.Select(group => group.ContextHash).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { first.Id, second.Id }.Order(),
            groups.SelectMany(group => group.Parts).SelectMany(part => part.GradingItemIds).Order());
    }

    [Fact]
    public async Task Missing_context_artifact_hash_does_not_use_item_hash_as_activity_hash()
    {
        await using var fixture = new Fixture();
        await fixture.AddChildWithSnapshotAsync(
            101,
            "The solution uses iteration.",
            contextArtifactMissingSha256: true);
        await fixture.AddChildWithSnapshotAsync(
            102,
            "I chose recursion and explained its termination.",
            contextArtifactMissingSha256: true);

        var result = await fixture.Handler.Handle(new PrepareGradingPackagesCommand(fixture.Run.Id), default);

        Assert.Equal(2, result.Packages.Count);
        var groups = Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Null(group.ContextHash));
    }

    [Theory]
    [InlineData("statement")]
    [InlineData("rubric")]
    public async Task Real_snapshots_with_different_activity_content_stay_in_separate_packages(string difference)
    {
        await using var fixture = new Fixture();
        var first = await fixture.AddChildWithSnapshotAsync(101, "The solution uses iteration.");
        var second = await fixture.AddChildWithSnapshotAsync(
            102, "I chose recursion and explained its termination.",
            statement: difference == "statement" ? "Compare two algorithms and justify their complexity." : "Describe an algorithm and justify your solution.",
            rubric: difference == "rubric" ? "Award six points for correctness and four for explanation." : "Award eight points for correctness and two for explanation.");
        fixture.Db.ChangeTracker.Clear();
        var snapshots = await fixture.Repository.ListLatestContextSnapshotsByItemsAsync([first.Id, second.Id], default);
        Assert.Equal(2, snapshots.Count);
        Assert.NotEqual(snapshots[first.Id].ContextHash, snapshots[second.Id].ContextHash);

        var result = await fixture.Handler.Handle(new PrepareGradingPackagesCommand(fixture.Run.Id), default);

        Assert.Equal(2, result.Packages.Count);
        var groups = Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.False(string.IsNullOrWhiteSpace(group.ContextHash)));
        Assert.Equal(2, groups.Select(group => group.ContextHash).Distinct().Count());
        Assert.Equal(new[] { first.Id, second.Id }.Order(),
            groups.SelectMany(group => group.Parts).SelectMany(part => part.GradingItemIds).Order());
        fixture.Db.ChangeTracker.Clear();
        foreach (var group in groups)
        {
            Assert.Equal(1, group.SelectedItems);
            var part = Assert.Single(group.Parts);
            var itemId = Assert.Single(part.GradingItemIds);
            Assert.Contains(result.Packages, package => package.PackageId == part.PackageId);
            var member = Assert.Single(await fixture.Store.ListMembersAsync(part.PackageId, default));
            Assert.Equal(itemId, member.GradingItemId);
            Assert.Equal(snapshots[itemId].ContextHash, member.ContextHash);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ConnectorDbContext Db { get; } = new(new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"activity-preparation-{Guid.NewGuid():N}").Options);
        public GradingReviewRepository Repository { get; }
        public GradingPackageStore Store { get; }
        public RecordingGradingPackageStoreProxy PackageStoreRecorder { get; }
        public GradingRun Run { get; } = GradingRun.Create("teacher-1");
        public GradingPackagePreparationChannel Channel { get; } = new();
        public PrepareGradingPackagesCommandHandler Handler { get; }
        public List<AssistedGradingItem> Items { get; } = [];

        public RecordingGradingReviewRepositoryProxy? ArtifactLookupRecorder { get; }

        public Fixture(
            int maxItems = 20,
            bool admin = false,
            bool recordArtifactLookups = false,
            IMoodleSubmissionFileGateway? submissionFileGateway = null,
            IGradingPackageArtifactCache? artifactCache = null,
            GradingPackageSubmissionReadCache? submissionReadCache = null)
        {
            Repository = new GradingReviewRepository(Db);
            Store = new GradingPackageStore(Db);
            var storeProxy = DispatchProxy.Create<IGradingPackageStore, RecordingGradingPackageStoreProxy>();
            PackageStoreRecorder = (RecordingGradingPackageStoreProxy)storeProxy;
            PackageStoreRecorder.Inner = Store;
            Db.Add(Run);
            IGradingReviewRepository handlerRepository = Repository;
            if (recordArtifactLookups)
            {
                var proxy = DispatchProxy.Create<IGradingReviewRepository, RecordingGradingReviewRepositoryProxy>();
                ArtifactLookupRecorder = (RecordingGradingReviewRepositoryProxy)proxy;
                ArtifactLookupRecorder.Inner = Repository;
                handlerRepository = proxy;
            }
            Handler = new PrepareGradingPackagesCommandHandler(
                handlerRepository, new CurrentUser(admin), storeProxy, Channel,
                Options.Create(new GradingLimitsOptions { MaxGradingPackageItems = maxItems }),
                submissionFileGateway,
                new GradingPackageDownloadLimiter(),
                artifactCache,
                submissionReadCache);
        }

        public async Task<AssistedGradingItem> AddChildAsync(
            int itemCount = 1,
            string? contextHash = null,
            long courseId = 10,
            long assignmentId = 501,
            string connectionId = "connection-1",
            string owner = "teacher-1",
            string instructions = "Use the rubric.",
            Guid? runId = null)
        {
            var batch = AssistedGradingBatch.Create(
                courseId, [assignmentId], owner, 321, itemCount,
                teacherInstructions: instructions,
                connectorClientId: "client-1", connectionAlias: "alias-1",
                moodleConnectionId: connectionId, gradingRunId: runId ?? Run.Id);
            await Repository.AddBatchAsync(batch, default);
            var start = Items.Count;
            for (var index = 0; index < itemCount; index++)
            {
                var item = AssistedGradingItem.Create(
                    batch.Id, courseId, assignmentId, 9000 + Items.Count, 100 + Items.Count, 1,
                    studentDisplayName: $"Student {Items.Count}");
                item.MarkAwaitingAiAnalysis(null);
                await Repository.AddItemAsync(item, default);
                // Seed the persisted ingestion identities; this test does not run ingestion.
                Db.Entry(item).Property(value => value.ContextHash).CurrentValue = contextHash ?? new string('a', 64);
                Db.Entry(item).Property(value => value.SubmissionContentHash).CurrentValue = new string('c', 64);
                Items.Add(item);
            }
            await Repository.SaveChangesAsync(default);
            return Items[start];
        }

        public async Task<AssistedGradingItem> AddChildWithSnapshotAsync(
            long studentId,
            string answer,
            string statement = "Describe an algorithm and justify your solution.",
            string rubric = "Award eight points for correctness and two for explanation.",
            string? contextArtifactContent = null,
            GradingSourceMetadata? contextSource = null,
            bool contextArtifactMissingSha256 = false,
            string? contextArtifactSourceUrl = null,
            bool contextArtifactPersistedSha256Missing = false,
            decimal maxGrade = 10m)
        {
            const string instructions = "Use the rubric.";
            var batch = AssistedGradingBatch.Create(
                10, [501], "teacher-1", 321, 1, teacherInstructions: instructions,
                connectorClientId: "client-1", connectionAlias: "alias-1",
                moodleConnectionId: "connection-1", gradingRunId: Run.Id);
            var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9000 + studentId, studentId, 1,
                studentDisplayName: $"Student {studentId}");
            var answerBytes = System.Text.Encoding.UTF8.GetBytes(answer);
            var answerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(answerBytes)).ToLowerInvariant();
            var artifact = new GradingArtifact(
                Guid.NewGuid(), item.Id, "submission_file", "answer.txt", "text/plain",
                answerHash, answerBytes.Length, "succeeded", answer, null, DateTimeOffset.UtcNow);
            var contextArtifactBytes = contextArtifactContent is null
                ? null
                : System.Text.Encoding.UTF8.GetBytes(contextArtifactContent);
            var contextArtifactSha256 = contextArtifactBytes is null
                ? null
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contextArtifactBytes)).ToLowerInvariant();
            var hasContextArtifact = contextArtifactSha256 is not null || contextArtifactMissingSha256;
            Guid? contextArtifactId = hasContextArtifact ? Guid.NewGuid() : null;
            var contextArtifactReferences = contextArtifactId is Guid artifactId
                ? new[]
                {
                    new GradingArtifactReferenceSnapshot(
                        artifactId,
                        "assignment_context",
                        "assignment.pdf",
                        "application/pdf",
                        contextArtifactSha256,
                        contextArtifactBytes?.LongLength,
                        ExtractionStatus.Succeeded,
                        1,
                        false,
                        64,
                        64,
                        contextSource)
                }
                : null;
            var context = GradingContext.Build(
                item.Id, batch.Id, "10", "501", item.SubmissionId!.Value.ToString(), studentId.ToString(),
                assignmentStatement: statement, criteria: "Correctness\nExplanation", rubricDescription: rubric,
                maxGrade, gradeScale: null, submissionText: answer,
                attachedFiles: [new GradingFileInfo("answer.txt", "text/plain", answerBytes.Length,
                    answerHash, answer, true, artifact.Id, "submission_file", "succeeded")],
                courseMaterials: null, teacherInstructions: instructions, gradingMode: "numeric",
                confirmedMaximumGrade: maxGrade,
                artifactReferences: contextArtifactReferences);
            Assert.True(context.HasMinimumContext);
            var snapshot = GradingContextSnapshotFactory.Create(item, context,
                new GradingContextOptions(TeacherInstructions: instructions));
            item.RecordContextSnapshot(snapshot);
            item.RecordSubmissionIntegrity(answerHash, []);
            item.MarkAwaitingAiAnalysis(null);
            await Repository.AddBatchAsync(batch, default);
            await Repository.AddItemAsync(item, default);
            await Repository.AddArtifactAsync(artifact, default);
            if (contextArtifactId is Guid contextId)
            {
                await Repository.AddArtifactAsync(new GradingArtifact(
                    contextId,
                    item.Id,
                    "assignment_context",
                    "assignment.pdf",
                    "application/pdf",
                    contextArtifactSha256,
                    contextArtifactBytes?.LongLength,
                    ExtractionStatus.Succeeded,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    contextArtifactSourceUrl ?? contextSource?.Title)
                {
                    Sha256 = contextArtifactPersistedSha256Missing ? null : contextArtifactSha256
                }, default);
            }
            await Repository.PublishAsync(snapshot, default);
            await Repository.SaveChangesAsync(default);
            Items.Add(item);
            return item;
        }

        public Task<GradingPackagePreparationResult> PrepareAsync(string mode) =>
            Handler.Handle(new PrepareGradingPackagesCommand(Run.Id, PackageMode: mode), default);

        public async Task AssertPersistedCoverageAsync(
            GradingPackagePreparationResult result, IReadOnlyCollection<AssistedGradingItem> expected)
        {
            // Clearing tracking ensures unsaved members cannot satisfy these assertions.
            Db.ChangeTracker.Clear();
            var expectedItemIds = expected.Select(item => item.Id).ToArray();
            var snapshots = await Repository.ListLatestContextSnapshotsByItemsAsync(expectedItemIds, default);
            var artifactsByItem = await Repository.ListArtifactsByItemsAsync(expectedItemIds, default);
            var groups = Assert.IsAssignableFrom<IReadOnlyList<GradingPackageActivityGroup>>(result.ActivityGroups);
            Assert.NotEmpty(groups);
            Assert.Equal(expected.Count, groups.Sum(group => group.SelectedItems));
            Assert.Equal(
                result.Packages.Select(package => package.PackageId).Order(),
                groups.SelectMany(group => group.Parts).Select(part => part.PackageId).Order());
            Assert.Equal(
                expected.Select(item => item.Id).Order(),
                groups.SelectMany(group => group.Parts).SelectMany(part => part.GradingItemIds).Order());
            foreach (var group in groups)
            {
                Assert.False(string.IsNullOrWhiteSpace(group.ActivityKey));
                Assert.NotEmpty(group.Parts);
                var groupItemIds = group.Parts.SelectMany(part => part.GradingItemIds).ToArray();
                var groupContextHashes = groupItemIds.Select(id =>
                {
                    var item = expected.Single(value => value.Id == id);
                    return snapshots.TryGetValue(id, out var snapshot) &&
                           GradingPackageMemberScope.HashesEqual(item.ContextHash, snapshot.ContextHash)
                        ? GradingPackageActivityContext.GetHash(snapshot, artifactsByItem.GetValueOrDefault(id))
                        : null;
                }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                Assert.Equal(groupContextHashes.Length == 1 ? groupContextHashes[0] : null, group.ContextHash);
                Assert.Equal(group.SelectedItems,
                    group.Parts.SelectMany(part => part.GradingItemIds).Distinct().Count());
                foreach (var part in group.Parts)
                {
                    Assert.NotEmpty(part.GradingItemIds);
                    var members = await Store.ListMembersAsync(part.PackageId, default);
                    Assert.Equal(members.Select(member => member.GradingItemId).Order(), part.GradingItemIds.Order());
                    Assert.All(part.GradingItemIds, id =>
                    {
                        var item = expected.Single(value => value.Id == id);
                        Assert.Equal(item.CourseId, group.CourseId);
                        Assert.Equal(item.AssignmentId, group.AssignmentId);
                    });
                }
            }
            var persistedIds = new List<Guid>();
            foreach (var descriptor in result.Packages)
            {
                var package = await Store.GetAsync(descriptor.PackageId, default);
                Assert.NotNull(package);
                var members = await Store.ListMembersAsync(descriptor.PackageId, default);
                Assert.NotEmpty(members);
                var packageItems = members.Select(member => expected.Single(item => item.Id == member.GradingItemId)).ToArray();
                Assert.Contains(package.BatchJobId, packageItems.Select(item => (Guid?)item.BatchId));
                Assert.All(members, member =>
                {
                    var item = expected.Single(value => value.Id == member.GradingItemId);
                    Assert.Equal(package.Id, member.PackageId);
                    Assert.Equal(item.ContextHash, member.ContextHash);
                    Assert.Equal(item.SubmissionContentHash, member.SubmissionContentHash);
                    Assert.Equal(item.SubmissionId, member.SubmissionId);
                    Assert.Equal(item.MoodleUserId, member.MoodleUserId);
                });
                persistedIds.AddRange(members.Select(member => member.GradingItemId));
            }
            Assert.Equal(expected.Select(item => item.Id).Order(), persistedIds.Order());
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class RecordingSubmissionFileGateway(
        IReadOnlyDictionary<string, byte[]> files,
        Exception? failure = null) : IMoodleSubmissionFileGateway
    {
        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        public Task<SubmissionFileDownloadResult> DownloadFileAsync(
            string userExternalId,
            string fileUrl,
            string filename,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls[fileUrl] = Calls.GetValueOrDefault(fileUrl) + 1;
            if (failure is not null) throw failure;
            var content = files[fileUrl];
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
            return Task.FromResult(new SubmissionFileDownloadResult(
                filename, "application/pdf", content.LongLength, hash, content, false));
        }
    }

    private sealed class RecordingArtifactCache : IGradingPackageArtifactCache
    {
        public List<(Guid ArtifactId, string Sha256, byte[] Content)> StoredEntries { get; } = [];

        public Task<GradingPackageArtifactCacheHit?> TryOpenReadAsync(
            Guid artifactId,
            string sha256,
            long? expectedSizeBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = StoredEntries.FirstOrDefault(entry => entry.ArtifactId == artifactId && entry.Sha256 == sha256);
            return Task.FromResult(stored.Content is null || expectedSizeBytes is > 0 && expectedSizeBytes != stored.Content.LongLength
                ? null
                : new GradingPackageArtifactCacheHit(
                    new MemoryStream(stored.Content, writable: false),
                    stored.Content.LongLength,
                    sha256));
        }

        public async Task StoreAsync(
            Guid artifactId,
            string sha256,
            long sizeBytes,
            Func<Stream, CancellationToken, Task> writer,
            CancellationToken cancellationToken)
        {
            await using var stream = new MemoryStream();
            await writer(stream, cancellationToken);
            var content = stream.ToArray();
            Assert.Equal(sizeBytes, content.LongLength);
            Assert.Equal(sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant());
            if (!StoredEntries.Any(entry => entry.ArtifactId == artifactId && entry.Sha256 == sha256))
            {
                StoredEntries.Add((artifactId, sha256, content));
            }
        }

        public Task<int> RemoveExpiredAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class CurrentUser(bool admin) : ICurrentUserContext
    {
        public string Subject => "teacher-1";
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => admin ? ["grading.admin"] : [];
        public bool HasScope(string scope) => Scopes.Contains(scope);
    }

    public class RecordingGradingReviewRepositoryProxy : DispatchProxy
    {
        public IGradingReviewRepository Inner { get; set; } = null!;
        public List<Guid[]> ArtifactQueries { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IGradingReviewRepository.ListArtifactsByItemsAsync))
            {
                ArtifactQueries.Add(((IReadOnlyCollection<Guid>)args![0]!).ToArray());
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    public class RecordingGradingPackageStoreProxy : DispatchProxy
    {
        public IGradingPackageStore Inner { get; set; } = null!;
        public int AcquireCalls { get; private set; }
        public int FindReusableCalls { get; private set; }
        public int GetOrCreateCalls { get; private set; }
        public int ListMembersCalls { get; private set; }

        public void Reset()
        {
            AcquireCalls = 0;
            FindReusableCalls = 0;
            GetOrCreateCalls = 0;
            ListMembersCalls = 0;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            switch (targetMethod.Name)
            {
                case nameof(IGradingPackageStore.AcquireAsync):
                    AcquireCalls++;
                    break;
                case nameof(IGradingPackageStore.FindReusableAsync):
                    FindReusableCalls++;
                    break;
                case nameof(IGradingPackageStore.GetOrCreateAsync):
                    GetOrCreateCalls++;
                    break;
                case nameof(IGradingPackageStore.ListMembersAsync):
                    ListMembersCalls++;
                    break;
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
