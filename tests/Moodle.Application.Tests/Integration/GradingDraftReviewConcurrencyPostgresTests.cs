using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class GradingDraftReviewConcurrencyPostgresTests
{
    [Fact]
    public async Task ConcurrentReviewConflict_RollsBackStaleReviewAndItsAuditEvent()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail())
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            $"review-conflict-{Guid.NewGuid():N}",
            321,
            totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(5m, 0.8m, "Synthetic initial draft.");
        await using (var seed = new ConnectorDbContext(options))
        {
            seed.GradingBatches.Add(batch);
            seed.GradingItems.Add(item);
            await seed.SaveChangesAsync();
        }

        await using var firstContext = new ConnectorDbContext(options);
        await using var secondContext = new ConnectorDbContext(options);
        var firstRepository = new GradingReviewRepository(firstContext);
        var secondRepository = new GradingReviewRepository(secondContext);
        var firstItem = await firstRepository.GetItemAsync(item.Id, CancellationToken.None);
        var secondItem = await secondRepository.GetItemAsync(item.Id, CancellationToken.None);
        Assert.NotNull(firstItem);
        Assert.NotNull(secondItem);
        firstItem.ApplyTeacherReview(7m, "Synthetic approved feedback.", "teacher-a", 321, "approved", "First revision.");
        await Task.Delay(10);
        secondItem.ApplyTeacherReview(6m, "Synthetic stale feedback.", "teacher-b", 654, "approved", "Stale revision.");

        var firstAudit = new MoodleAuditLogRepository(firstContext);
        var secondAudit = new MoodleAuditLogRepository(secondContext);
        await firstAudit.AddAsync(CreateReviewLog(batch.Id, "teacher-a", 7m), CancellationToken.None);
        await secondAudit.AddAsync(CreateReviewLog(batch.Id, "teacher-b", 6m), CancellationToken.None);

        await firstRepository.SaveChangesForDraftReviewAsync(CancellationToken.None);
        await Assert.ThrowsAsync<GradingDraftVersionConflictException>(() =>
            secondRepository.SaveChangesForDraftReviewAsync(CancellationToken.None));

        await using var verification = new ConnectorDbContext(options);
        var persistedItem = await verification.GradingItems
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == item.Id);
        Assert.Equal(7m, persistedItem.FinalGrade);
        Assert.Equal("Synthetic approved feedback.", persistedItem.FinalFeedback);
        var audit = Assert.Single(await verification.MoodleAuditLogs
            .AsNoTracking()
            .Where(log => log.BatchJobId == batch.Id)
            .ToArrayAsync());
        Assert.Equal("teacher-a", audit.ActorSubject);
    }

    [Fact]
    public async Task ScaleRefreshConcurrentWithReview_RollsBackSnapshotAndAudit()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail())
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(10, [501], $"scale-review-{Guid.NewGuid():N}", 321, 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(5m, 0.8m, "Synthetic initial draft.");
        var originalSnapshot = CreateSnapshot(item, 1, 10m, "scale-v1");
        item.RecordContextSnapshot(originalSnapshot);
        await using (var seed = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(seed);
            await repository.AddBatchAsync(batch, CancellationToken.None);
            await repository.AddItemAsync(item, CancellationToken.None);
            await repository.PublishAsync(originalSnapshot, CancellationToken.None);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var reviewContext = new ConnectorDbContext(options);
        await using var refreshContext = new ConnectorDbContext(options);
        var reviewRepository = new GradingReviewRepository(reviewContext);
        var refreshRepository = new GradingReviewRepository(refreshContext);
        var reviewItem = await reviewRepository.GetItemAsync(item.Id, CancellationToken.None);
        var refreshItem = await refreshRepository.GetItemAsync(item.Id, CancellationToken.None);
        Assert.NotNull(reviewItem);
        Assert.NotNull(refreshItem);
        reviewItem.ApplyTeacherReview(7m, "Synthetic approved feedback.", "teacher-a", 321, "approved", "Scale review race.");
        var refreshedSnapshot = originalSnapshot.WithGradingScale(
            new GradingScaleSnapshot(40m, null, null, "numeric", 0m, "activity_gradebook", "scale-v2"));
        refreshItem.InvalidateDraftForScaleRefresh("Escala alterada.");
        refreshItem.RecordContextSnapshot(refreshedSnapshot);
        await refreshRepository.PublishAsync(refreshedSnapshot, CancellationToken.None);
        await new MoodleAuditLogRepository(reviewContext).AddAsync(
            CreateReviewLog(batch.Id, "teacher-a", 7m),
            CancellationToken.None);
        await new MoodleAuditLogRepository(refreshContext).AddAsync(new MoodleAuditLog
        {
            BatchJobId = batch.Id,
            ToolName = "refresh_grading_scale",
            ActorSubject = "teacher-b",
            RequestSanitizedJson = "{}",
            ResponseSummaryJson = "{}",
            Status = "grading_scale_refreshed"
        }, CancellationToken.None);

        await reviewRepository.SaveChangesForDraftReviewAsync(CancellationToken.None);
        await Assert.ThrowsAsync<GradingDraftVersionConflictException>(() =>
            refreshRepository.SaveChangesForGradingContextRefreshAsync(CancellationToken.None));

        await using var verification = new ConnectorDbContext(options);
        var persistedItem = await verification.GradingItems.AsNoTracking().SingleAsync(candidate => candidate.Id == item.Id);
        Assert.Equal(7m, persistedItem.FinalGrade);
        Assert.Equal(originalSnapshot.ContextHash, persistedItem.ContextHash);
        Assert.Equal(1, await verification.GradingContextSnapshots.CountAsync(snapshot => snapshot.GradingItemId == item.Id));
        var audit = await verification.MoodleAuditLogs.AsNoTracking().Where(log => log.BatchJobId == batch.Id).ToArrayAsync();
        Assert.Equal("teacher-a", Assert.Single(audit).ActorSubject);
    }

    [Fact]
    public async Task ScaleRefreshConcurrentWithPublicationIntent_ReturnsConflict()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(GetConnectionStringOrFail())
            .Options;
        await using (var schemaDb = new ConnectorDbContext(options))
        {
            await schemaDb.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(10, [501], $"scale-publication-{Guid.NewGuid():N}", 321, 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(5m, 0.8m, "Synthetic initial draft.");
        item.ApplyTeacherReview(5m, "Synthetic approved feedback.", "teacher-1", 321, "approved", "Synthetic review.");
        var originalSnapshot = CreateSnapshot(item, 1, 10m, "scale-v1");
        item.RecordContextSnapshot(originalSnapshot);
        await using (var seed = new ConnectorDbContext(options))
        {
            var repository = new GradingReviewRepository(seed);
            await repository.AddBatchAsync(batch, CancellationToken.None);
            await repository.AddItemAsync(item, CancellationToken.None);
            await repository.PublishAsync(originalSnapshot, CancellationToken.None);
            await repository.SaveChangesAsync(CancellationToken.None);
        }

        await using var publisherContext = new ConnectorDbContext(options);
        await using var refreshContext = new ConnectorDbContext(options);
        var publisherRepository = new GradingReviewRepository(publisherContext);
        var refreshRepository = new GradingReviewRepository(refreshContext);
        var publisherItem = await publisherRepository.GetItemAsync(item.Id, CancellationToken.None);
        var refreshItem = await refreshRepository.GetItemAsync(item.Id, CancellationToken.None);
        Assert.NotNull(publisherItem);
        Assert.NotNull(refreshItem);
        Assert.True(publisherItem.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow));
        var refreshedSnapshot = originalSnapshot.WithGradingScale(
            new GradingScaleSnapshot(40m, null, null, "numeric", 0m, "activity_gradebook", "scale-v2"));
        refreshItem.InvalidateDraftForScaleRefresh("Escala alterada.");
        refreshItem.RecordContextSnapshot(refreshedSnapshot);
        await refreshRepository.PublishAsync(refreshedSnapshot, CancellationToken.None);

        await publisherRepository.SaveChangesAsync(CancellationToken.None);
        await Assert.ThrowsAsync<GradingDraftVersionConflictException>(() =>
            refreshRepository.SaveChangesForGradingContextRefreshAsync(CancellationToken.None));

        await using var verification = new ConnectorDbContext(options);
        var persistedItem = await verification.GradingItems.AsNoTracking().SingleAsync(candidate => candidate.Id == item.Id);
        Assert.NotNull(persistedItem.CommitWriteIntentAt);
        Assert.Equal(originalSnapshot.ContextHash, persistedItem.ContextHash);
        Assert.Equal(1, await verification.GradingContextSnapshots.CountAsync(snapshot => snapshot.GradingItemId == item.Id));
    }

    private static GradingContextSnapshot CreateSnapshot(
        AssistedGradingItem item,
        int version,
        decimal maximumGrade,
        string scaleVersion) =>
        GradingContextSnapshot.Create(
            item.Id,
            item.BatchId,
            new MoodleAssignmentReference(item.CourseId, item.AssignmentId, null),
            new MoodleSubmissionReference(item.SubmissionId!.Value),
            new MoodleUserReference(item.MoodleUserId),
            item.AttemptNumber,
            version,
            "Synthetic assignment",
            "Synthetic statement",
            [],
            null,
            new GradingScaleSnapshot(maximumGrade, null, null, "numeric", 0m, "test", scaleVersion),
            [],
            [],
            new GradingExtractionSummary("missing", 0, false, 0, 0, null),
            new GradingEvidenceCoverage(0, 0, 0, 0, 0, 0, false),
            null,
            [],
            [],
            false);

    private static MoodleAuditLog CreateReviewLog(Guid batchId, string actor, decimal grade) => new()
    {
        BatchJobId = batchId,
        ToolName = "atualizar_rascunho_correcao",
        ActorSubject = actor,
        RequestSanitizedJson = $$"""{"grade":{{grade}}}""",
        Status = "draft_reviewed"
    };

    private static string GetConnectionStringOrFail()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "PostgreSQL integration tests must run through the Docker test gate; the database gate cannot silently skip.");
        return connectionString!;
    }
}
