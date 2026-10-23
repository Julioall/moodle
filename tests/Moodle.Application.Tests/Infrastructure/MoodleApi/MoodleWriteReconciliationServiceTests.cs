using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure.MoodleApi;

public sealed class MoodleWriteReconciliationServiceTests
{
    [Fact]
    public async Task ReconcileItem_AppliedReadbackWorksAfterGlobalActionWasMarkedFailed()
    {
        await using var fixture = await CreateFixtureAsync(
            observedGrade: 8m,
            observedFeedback: "Feedback final aprovado.",
            feedbackFieldPresent: true,
            terminalActionStatus: PendingActionStatus.Failed);

        var result = await fixture.Service.ReconcileAsync(
            fixture.Action.Id,
            "auto",
            fixture.Item.Id,
            CancellationToken.None);

        Assert.Equal("reconciled", result.Status);
        Assert.Equal("applied", result.ItemOutcome);
        var persistedItem = await fixture.Db.GradingItems.SingleAsync(item => item.Id == fixture.Item.Id);
        Assert.Equal(GradingCommitStatus.Succeeded, persistedItem.CommitStatus);
        Assert.Equal(PendingActionStatus.Failed, fixture.Action.Status);
        Assert.Equal("Succeeded", (await fixture.Repository.GetPublicationClaimStatusesAsync(
            fixture.PublicationId,
            CancellationToken.None))[fixture.Item.Id]);
        var attempt = await fixture.Db.GradingPublicationAttemptEvents.SingleAsync(
            item => item.PendingActionId == fixture.Action.Id && item.GradingItemId == fixture.Item.Id);
        Assert.Equal("item_reconciled_target_observed", attempt.EventType);
        Assert.Single(await fixture.Db.MoodleAuditLogs.ToArrayAsync());
    }

    [Fact]
    public async Task ReconcileItem_IncompleteReadbackRemainsUnknownAndRecordsEvidence()
    {
        await using var fixture = await CreateFixtureAsync(
            observedGrade: 8m,
            observedFeedback: "Feedback final aprovado.",
            feedbackFieldPresent: false);

        var result = await fixture.Service.ReconcileAsync(
            fixture.Action.Id,
            "auto",
            fixture.Item.Id,
            CancellationToken.None);

        Assert.Equal("inconclusive", result.ItemOutcome);
        var persistedItem = await fixture.Db.GradingItems.SingleAsync(item => item.Id == fixture.Item.Id);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, persistedItem.CommitStatus);
        Assert.Equal("ExecutionUnknown", (await fixture.Repository.GetPublicationClaimStatusesAsync(
            fixture.PublicationId,
            CancellationToken.None))[fixture.Item.Id]);
        var attempt = await fixture.Db.GradingPublicationAttemptEvents.SingleAsync(
            item => item.PendingActionId == fixture.Action.Id && item.GradingItemId == fixture.Item.Id);
        Assert.Equal("item_reconciliation_inconclusive", attempt.EventType);
        Assert.Equal("remote_readback_inconclusive", attempt.EvidenceKind);
        Assert.Single(await fixture.Db.MoodleAuditLogs.ToArrayAsync());
    }

    [Fact]
    public async Task ReconcileItem_ObservedStateMatchingNeitherTargetNorBaselineRemainsConflict()
    {
        await using var fixture = await CreateFixtureAsync(
            observedGrade: 3m,
            observedFeedback: "Alteração externa.",
            feedbackFieldPresent: true);

        var result = await fixture.Service.ReconcileAsync(
            fixture.Action.Id,
            "auto",
            fixture.Item.Id,
            CancellationToken.None);

        Assert.Equal("conflict", result.ItemOutcome);
        var persistedItem = await fixture.Db.GradingItems.SingleAsync(item => item.Id == fixture.Item.Id);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, persistedItem.CommitStatus);
        Assert.Equal("ExecutionUnknown", (await fixture.Repository.GetPublicationClaimStatusesAsync(
            fixture.PublicationId,
            CancellationToken.None))[fixture.Item.Id]);
        var attempt = await fixture.Db.GradingPublicationAttemptEvents.SingleAsync(
            item => item.PendingActionId == fixture.Action.Id && item.GradingItemId == fixture.Item.Id);
        Assert.Equal("item_reconciliation_conflict", attempt.EventType);
        Assert.Equal("remote_conflict", attempt.EvidenceKind);
        Assert.Single(await fixture.Db.MoodleAuditLogs.ToArrayAsync());
    }

    private static async Task<Fixture> CreateFixtureAsync(
        decimal observedGrade,
        string observedFeedback,
        bool feedbackFieldPresent,
        PendingActionStatus? terminalActionStatus = null)
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"moodle-write-reconcile-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new ConnectorDbContext(options);
        var subject = "teacher-reconcile-test";
        const string connectionId = "reconcile-test-connection";
        const string workerId = "reconcile-test-worker";
        var now = DateTimeOffset.UtcNow;
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            subject,
            321,
            totalItems: 1,
            moodleConnectionId: connectionId);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.95m, "Rascunho de teste.");
        item.ApplyTeacherReview(8m, "Feedback final aprovado.", subject, 321, "approved", maxGrade: 10m);
        Assert.True(item.TryMarkCommitWriteIntent(now));
        item.MarkCommitExecutionUnknown("Resposta perdida.");

        var publicationId = Guid.NewGuid();
        var connectionKey = $"connection:{connectionId}";
        var approvedItem = new GradingLaunchPayloadItem(
            item.Id,
            "10",
            "501",
            "101",
            8m,
            "Feedback final aprovado.",
            0,
            GradingDraftVersionHash.Compute(item),
            GradeOperation: "set_grade",
            BaselineHasGrade: true,
            BaselineGrade: 5m,
            WorkflowMode: "disabled",
            ReleaseState: "not_applicable",
            VisibilityState: "unknown");
        var baseline = new AssignmentExistingGrade(
            "501",
            "101",
            5m,
            HasGrade: true,
            Feedback: "Feedback baseline.",
            GradeMax: 10m,
            FeedbackObserved: true);
        approvedItem = approvedItem with
        {
            PreflightHash = GradingPublicationGradebookProjection.ComputePreflightHash(
                connectionKey,
                approvedItem,
                baseline)
        };
        var payload = new GradingLaunchPayload(
            batch.Id,
            [approvedItem],
            PublicationId: publicationId,
            ConnectionKey: connectionKey,
            ContractVersion: GradingLaunchPayload.CurrentContractVersion);
        var action = new PendingMoodleAction
        {
            ToolName = "confirmar_lancamento_lote_moodle",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            CreatedBySubject = subject,
            CreatedByMoodleUserId = 321,
            CourseId = 10,
            PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR_PUBLICACAO",
            ExpiresAt = now.AddHours(1),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        action.Authorize(subject, now);
        Assert.True(action.BeginExecution(workerId, now, TimeSpan.FromMinutes(5)));
        action.MarkExecutionUnknown("Resultado remoto desconhecido.");
        if (terminalActionStatus == PendingActionStatus.Failed)
        {
            action.MarkFailed("Falha global legada.");
        }

        db.GradingBatches.Add(batch);
        db.GradingItems.Add(item);
        db.PendingMoodleActions.Add(action);
        db.GradingPublicationClaims.Add(new GradingPublicationClaimEntity
        {
            PublicationId = publicationId,
            PendingActionId = action.Id,
            GradingItemId = item.Id,
            ConnectionKey = connectionKey,
            AssignmentId = 501,
            MoodleUserId = 101,
            AttemptNumber = 0,
            WorkerId = workerId,
            FencingToken = action.ExecutionAttemptCount,
            Status = "ExecutionUnknown",
            ExpiresAt = DateTimeOffset.MaxValue
        });
        await db.SaveChangesAsync();

        var repository = new GradingReviewRepository(db);
        var pendingActions = new TestPendingActionRepository(action, db);
        var user = new TestCurrentUser(subject);
        var assignmentGrade = new AssignmentExistingGrade(
            "501",
            "101",
            observedGrade,
            HasGrade: true,
            Feedback: null,
            GradeMax: 10m);
        var gradebook = new CourseGradebook(
            "10",
            "101",
            [new GradebookItem(
                "501",
                "Atividade 501",
                "mod",
                "assign",
                null,
                observedGrade,
                observedGrade.ToString(System.Globalization.CultureInfo.InvariantCulture),
                0m,
                10m,
                null,
                observedFeedback,
                "html",
                null,
                null,
                "321",
                ItemInstance: "501",
                FeedbackFieldPresent: feedbackFieldPresent)])
        {
            ReadAt = now
        };
        var service = new MoodleWriteReconciliationService(
            pendingActions,
            user,
            repository,
            new TestAssignmentGradeReadGateway(assignmentGrade),
            new TestGradebookGateway(gradebook),
            new TestSubmissionStatusGateway(),
            backgroundConnectionContextRestorer: new TestConnectionRestorer());

        return new Fixture(db, repository, action, item, publicationId, service);
    }

    private sealed record Fixture(
        ConnectorDbContext Db,
        GradingReviewRepository Repository,
        PendingMoodleAction Action,
        AssistedGradingItem Item,
        Guid PublicationId,
        MoodleWriteReconciliationService Service) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class TestCurrentUser(string subject) : ICurrentUserContext
    {
        public string Subject { get; } = subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes { get; } = ["moodle.write.assignments.grade"];
        public bool HasScope(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);
    }

    private sealed class TestPendingActionRepository(PendingMoodleAction action, ConnectorDbContext db)
        : IPendingMoodleActionRepository
    {
        public Task AddAsync(PendingMoodleAction pendingAction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(id == action.Id ? action : null);

        public Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(
            Guid id,
            string confirmedBySubject,
            DateTimeOffset confirmedAt,
            MoodleAuditLog confirmationAudit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<PendingActionReconciliationClaimResult> TryResolveExecutionUnknownWithAuditAsync(
            Guid id,
            PendingActionStatus resolvedStatus,
            MoodleAuditLog reconciliationAudit,
            CancellationToken cancellationToken)
        {
            if (id != action.Id || action.Status != PendingActionStatus.ExecutionUnknown)
            {
                return new PendingActionReconciliationClaimResult(false, action.Status, null);
            }

            action.ResolveExecutionUnknown(resolvedStatus);
            db.MoodleAuditLogs.Add(reconciliationAudit);
            await db.SaveChangesAsync(cancellationToken);
            return new PendingActionReconciliationClaimResult(true, resolvedStatus, reconciliationAudit.Id.ToString("N"));
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
    }

    private sealed class TestAssignmentGradeReadGateway(AssignmentExistingGrade grade) : IMoodleAssignmentGradeReadGateway
    {
        public Task<AssignmentExistingGrade?> GetExistingGradeAsync(
            string userExternalId,
            string assignmentId,
            string studentId,
            CancellationToken cancellationToken) => Task.FromResult<AssignmentExistingGrade?>(grade);
    }

    private sealed class TestGradebookGateway(CourseGradebook gradebook) : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            CancellationToken cancellationToken) => Task.FromResult(gradebook);

        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            GradebookReadOptions readOptions,
            CancellationToken cancellationToken) => Task.FromResult(gradebook);
    }

    private sealed class TestSubmissionStatusGateway : IMoodleAssignmentSubmissionStatusGateway
    {
        public Task<AssignmentSubmissionAttemptStatus?> GetSubmissionStatusAsync(
            string userExternalId,
            string assignmentId,
            string studentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AssignmentSubmissionAttemptStatus?>(new(assignmentId, studentId, 0, "submitted"));
    }

    private sealed class TestConnectionRestorer : IMoodleBackgroundConnectionContextRestorer
    {
        public Task<bool> TryRestoreAsync(string connectionId, string subject, CancellationToken cancellationToken) =>
            Task.FromResult(connectionId == "reconcile-test-connection");
    }
}
