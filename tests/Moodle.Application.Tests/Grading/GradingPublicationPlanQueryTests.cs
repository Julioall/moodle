using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPublicationPlanQueryTests
{
    [Fact]
    public async Task PlanByRun_Groups95ItemsByActivityAndPagesWithoutOmissions()
    {
        await using var db = CreateDbContext();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501, 502, 503], "teacher-1", 321, 100, gradingRunId: run.Id);
        var items = Enumerable.Range(1, 95)
            .Select(index => CreateEligibleItem(batch, 501, index))
            .Concat(Enumerable.Range(96, 2).Select(index => CreateEligibleItem(batch, 502, index)))
            .Concat(Enumerable.Range(98, 3).Select(index => CreateEligibleItem(batch, 503, index)))
            .ToArray();
        db.GradingRuns.Add(run);
        db.GradingBatches.Add(batch);
        db.GradingItems.AddRange(items);
        await db.SaveChangesAsync();

        var sut = CreateHandler(db, "teacher-1", recommendedMaxItems: 20);

        var firstPage = await sut.Handle(
            new GetGradingPublicationPlanQuery(run.Id, Page: 1, PageSize: 2),
            CancellationToken.None);
        var secondPage = await sut.Handle(
            new GetGradingPublicationPlanQuery(run.Id, Page: 2, PageSize: 2),
            CancellationToken.None);
        var thirdPage = await sut.Handle(
            new GetGradingPublicationPlanQuery(run.Id, Page: 3, PageSize: 2),
            CancellationToken.None);
        var remainingPage = await sut.Handle(
            new GetGradingPublicationPlanQuery(run.Id, Page: 4, PageSize: 2),
            CancellationToken.None);
        var allGroups = firstPage.Groups.Concat(secondPage.Groups).Concat(thirdPage.Groups).Concat(remainingPage.Groups).ToArray();

        Assert.Equal(7, firstPage.TotalGroups);
        Assert.True(firstPage.HasMore);
        Assert.True(secondPage.HasMore);
        Assert.True(thirdPage.HasMore);
        Assert.False(remainingPage.HasMore);
        Assert.Equal(new[] { 20, 20, 20, 20, 15 }, allGroups
            .Where(group => group.AssignmentId == "501")
            .Select(group => group.GradingItemIds.Count));
        Assert.Equal(new[] { "501", "501", "501", "501", "501", "502", "503" },
            allGroups.Select(group => group.AssignmentId));
        Assert.Equal(items.Select(item => item.Id).Order().ToArray(),
            allGroups.SelectMany(group => group.GradingItemIds).Order().ToArray());
        Assert.Equal(items.Length, allGroups.SelectMany(group => group.GradingItemIds).Distinct().Count());
        Assert.All(allGroups, group => Assert.InRange(group.GradingItemIds.Count, 1, 20));
        Assert.Contains(firstPage.Warnings, warning => warning.Contains("revalid", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(20, firstPage.RecommendedMaxItems);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public void CreateGroups_IsStableWhenInputOrderChanges()
    {
        var batch = AssistedGradingBatch.Create(10, [501, 502], "teacher-1", 321, 40);
        var items = Enumerable.Range(1, 35)
            .Select(index => CreateEligibleItem(batch, index <= 23 ? 501 : 502, index))
            .ToArray();

        var forward = GradingPublicationPlanGrouping.CreateGroups(items, maxItems: 7);
        var reverse = GradingPublicationPlanGrouping.CreateGroups(items.Reverse().ToArray(), maxItems: 7);

        Assert.Equal(
            forward.Select(DescribeGroup),
            reverse.Select(DescribeGroup));
    }

    [Fact]
    public async Task Plan_ExcludesTerminalUnknownWriteIntentAndActiveActionTargets()
    {
        await using var db = CreateDbContext();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 5, gradingRunId: run.Id);
        var eligible = CreateEligibleItem(batch, 501, 1);
        var succeeded = CreateReviewedItem(batch, 501, 2);
        succeeded.MarkCommitSucceeded();
        var unknown = CreateReviewedItem(batch, 501, 3);
        Assert.True(unknown.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow));
        unknown.MarkCommitExecutionUnknown("Synthetic uncertain result.", "moodle_timeout");
        var pendingIntent = CreateReviewedItem(batch, 501, 4);
        Assert.True(pendingIntent.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow));
        var activeActionTarget = CreateEligibleItem(batch, 501, 5);
        var activeAction = CreateActiveAction(batch, activeActionTarget);
        db.GradingRuns.Add(run);
        db.GradingBatches.Add(batch);
        db.GradingItems.AddRange(eligible, succeeded, unknown, pendingIntent, activeActionTarget);
        db.PendingMoodleActions.Add(activeAction);
        await db.SaveChangesAsync();
        var activeActionCreatedAt = activeAction.CreatedAt;
        var itemUpdatedAt = eligible.UpdatedAt;

        var result = await CreateHandler(db, "teacher-1").Handle(
            new GetGradingPublicationPlanQuery(run.Id),
            CancellationToken.None);

        var plannedIds = Assert.Single(result.Groups).GradingItemIds;
        Assert.Equal([eligible.Id], plannedIds);
        var actionReference = Assert.Single(result.ActiveActions);
        Assert.Equal(activeAction.Id, actionReference.PendingActionId);
        Assert.Equal(PendingActionStatus.PendingConfirmation.ToString(), actionReference.Status);
        Assert.Equal(activeActionCreatedAt, (await db.PendingMoodleActions.SingleAsync()).CreatedAt);
        Assert.Equal(itemUpdatedAt, (await db.GradingItems.SingleAsync(item => item.Id == eligible.Id)).UpdatedAt);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown,
            (await db.GradingItems.SingleAsync(item => item.Id == unknown.Id)).CommitStatus);
        Assert.Contains(result.Warnings, warning => warning.Contains("active", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Plan_DoesNotExposeInaccessibleActiveActionButStillExcludesItsTarget()
    {
        await using var db = CreateDbContext();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: run.Id);
        var item = CreateEligibleItem(batch, 501, 1);
        var otherUsersAction = CreateActiveAction(batch, item, createdBySubject: "teacher-2");
        db.GradingRuns.Add(run);
        db.GradingBatches.Add(batch);
        db.GradingItems.Add(item);
        db.PendingMoodleActions.Add(otherUsersAction);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db, "teacher-1").Handle(
            new GetGradingPublicationPlanQuery(run.Id),
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Empty(result.ActiveActions);
    }

    [Fact]
    public async Task Plan_RejectsCrossUserRunAccess()
    {
        await using var db = CreateDbContext();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: run.Id);
        db.GradingRuns.Add(run);
        db.GradingBatches.Add(batch);
        db.GradingItems.Add(CreateEligibleItem(batch, 501, 1));
        await db.SaveChangesAsync();

        var sut = CreateHandler(db, "teacher-2");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.Handle(
            new GetGradingPublicationPlanQuery(run.Id),
            CancellationToken.None));
    }

    [Fact]
    public async Task Plan_RejectsCrossUserBatchAccess()
    {
        await using var db = CreateDbContext();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1);
        db.GradingBatches.Add(batch);
        db.GradingItems.Add(CreateEligibleItem(batch, 501, 1));
        await db.SaveChangesAsync();

        var sut = CreateHandler(db, "teacher-2");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sut.Handle(
            new GetGradingPublicationPlanQuery(batch.Id),
            CancellationToken.None));
    }

    [Fact]
    public async Task PlanByBatch_FindsRunWideActiveActionForItsTarget()
    {
        await using var db = CreateDbContext();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: run.Id);
        var item = CreateEligibleItem(batch, 501, 1);
        var action = CreateActiveAction(batch, item, payloadBatchJobId: run.Id);
        db.GradingRuns.Add(run);
        db.GradingBatches.Add(batch);
        db.GradingItems.Add(item);
        db.PendingMoodleActions.Add(action);
        await db.SaveChangesAsync();

        var result = await CreateHandler(db, "teacher-1").Handle(
            new GetGradingPublicationPlanQuery(batch.Id),
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(action.Id, Assert.Single(result.ActiveActions).PendingActionId);
    }

    [Fact]
    public async Task PlanByRun_WithIncompleteDeclaredCoverageReturnsNoGroups()
    {
        await using var db = CreateDbContext();
        var run = GradingRun.Create("teacher-1");
        run.SetExpectedCoverage(expectedItemCount: 2, expectedBatchCount: 2);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: run.Id);
        db.GradingRuns.Add(run);
        db.GradingBatches.Add(batch);
        db.GradingItems.Add(CreateEligibleItem(batch, 501, 1));
        await db.SaveChangesAsync();

        var result = await CreateHandler(db, "teacher-1").Handle(
            new GetGradingPublicationPlanQuery(run.Id),
            CancellationToken.None);

        Assert.Empty(result.Groups);
        Assert.Equal(0, result.TotalGroups);
        Assert.Contains(result.Warnings, warning => warning.Contains("Cobertura incompleta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_RejectsConfiguredRecommendationOutsideSupportedRange()
    {
        await using var db = CreateDbContext();

        Assert.Throws<OptionsValidationException>(() => CreateHandler(db, "teacher-1", recommendedMaxItems: 0));
    }

    private static GetGradingPublicationPlanQueryHandler CreateHandler(
        ConnectorDbContext db,
        string subject,
        int recommendedMaxItems = 20) =>
        new(
            new GradingReviewRepository(db),
            new PendingMoodleActionRepository(db),
            new TestCurrentUserContext(subject),
            Microsoft.Extensions.Options.Options.Create(new GradingLimitsOptions
            {
                PublicationRecommendedMaxItems = recommendedMaxItems
            }));

    private static ConnectorDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"grading-publication-plan-{Guid.NewGuid():N}")
            .Options);

    private static AssistedGradingItem CreateEligibleItem(
        AssistedGradingBatch batch,
        long assignmentId,
        long studentId)
    {
        var item = AssistedGradingItem.Create(batch.Id, batch.CourseId, assignmentId, 10_000 + studentId, studentId, 0);
        item.SetDraft(null, 0.9m, "Synthetic feedback only.");
        return item;
    }

    private static string DescribeGroup(GradingPublicationPlanGroup group) =>
        $"{group.AssignmentId}/{group.GroupIndex}/{string.Join(',', group.GradingItemIds)}";

    private static AssistedGradingItem CreateReviewedItem(
        AssistedGradingBatch batch,
        long assignmentId,
        long studentId)
    {
        var item = CreateEligibleItem(batch, assignmentId, studentId);
        item.ApplyTeacherReview(null, "Reviewed synthetic feedback.", "teacher-1", 321);
        return item;
    }

    private static PendingMoodleAction CreateActiveAction(
        AssistedGradingBatch batch,
        AssistedGradingItem item,
        string createdBySubject = "teacher-1",
        Guid? payloadBatchJobId = null)
    {
        var payload = new GradingLaunchPayload(
            payloadBatchJobId ?? batch.Id,
            [new GradingLaunchPayloadItem(
                item.Id,
                item.CourseId.ToString(),
                item.AssignmentId.ToString(),
                item.MoodleUserId.ToString(),
                null,
                "Synthetic active feedback.",
                item.AttemptNumber,
                "synthetic-version")]);
        return new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = createdBySubject,
            PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            PreviewJson = "{}",
            ConfirmationText = string.Empty,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
    }

    private sealed class TestCurrentUserContext(string subject) : ICurrentUserContext
    {
        public string Subject { get; } = subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }
}
