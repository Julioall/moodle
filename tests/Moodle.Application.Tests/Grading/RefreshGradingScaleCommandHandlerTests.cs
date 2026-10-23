using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Grading;

public sealed class RefreshGradingScaleCommandHandlerTests
{
    [Fact]
    public async Task Refresh_AlteraSomenteItensSelecionadosEInvalidaPreviewPendente()
    {
        await using var db = CreateDbContext();
        var repository = new GradingReviewRepository(db);
        var fixture = await SeedAsync(db, repository, itemCount: 2);
        var selected = fixture.Items[0];
        var sibling = fixture.Items[1];
        var siblingHash = sibling.ContextHash;
        var publicationId = Guid.NewGuid();
        var payload = new GradingLaunchPayload(
            fixture.Batch.Id,
            [new GradingLaunchPayloadItem(
                selected.Id,
                selected.CourseId.ToString(),
                selected.AssignmentId.ToString(),
                selected.MoodleUserId.ToString(),
                selected.SuggestedGrade,
                selected.DraftFeedback ?? string.Empty,
                selected.AttemptNumber,
                GradingDraftVersionHash.Compute(selected),
                selected.ContextHash)],
            PublicationId: publicationId);
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        };
        db.PendingMoodleActions.Add(action);
        db.GradingPublicationClaims.Add(new GradingPublicationClaimEntity
        {
            PublicationId = publicationId,
            PendingActionId = action.Id,
            GradingItemId = selected.Id,
            ConnectionKey = "connection:test",
            AssignmentId = selected.AssignmentId,
            MoodleUserId = selected.MoodleUserId,
            AttemptNumber = selected.AttemptNumber ?? 0,
            Status = "AwaitingConfirmation",
            ExpiresAt = action.ExpiresAt
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler(db, repository);
        var result = await handler.Handle(
            new RefreshGradingScaleCommand(
                fixture.Batch.Id,
                501,
                [Expectation(selected, fixture.Snapshots[selected.Id])]),
            CancellationToken.None);

        var refreshed = await db.GradingItems.SingleAsync(item => item.Id == selected.Id);
        var unchanged = await db.GradingItems.SingleAsync(item => item.Id == sibling.Id);
        var latestDocument = await db.GradingContextSnapshots
            .SingleAsync(snapshot => snapshot.GradingItemId == selected.Id && snapshot.Version == 2);
        var latestSnapshot = GradingContextSnapshot.DeserializeCanonicalPayload(latestDocument.PayloadJson);

        Assert.Equal("refreshed", result.Status);
        _ = Assert.Single(result.RefreshedItems);
        Assert.Equal(action.Id, Assert.Single(result.InvalidatedPreviewActionIds));
        Assert.Equal(siblingHash, unchanged.ContextHash);
        Assert.Equal(GradingItemStatus.Pending, refreshed.Status);
        Assert.Null(refreshed.SuggestedGrade);
        Assert.Equal(2, refreshed.ContextVersion);
        Assert.Equal(40m, latestSnapshot.GradingScale!.MaximumGrade);
        Assert.Equal(result.ScaleVersion, latestSnapshot.GradingScale.Version);
        Assert.Equal(PendingActionStatus.Cancelled,
            (await db.PendingMoodleActions.SingleAsync(candidate => candidate.Id == action.Id)).Status);
        Assert.Equal("Released", (await db.GradingPublicationClaims.SingleAsync()).Status);
        Assert.Contains(await db.MoodleAuditLogs.ToArrayAsync(), log => log.Status == "grading_scale_refreshed");
    }

    [Fact]
    public async Task Refresh_ConflitoComRevisaoDuranteLeituraNaoApagaDecisao()
    {
        await using var db = CreateDbContext();
        var repository = new GradingReviewRepository(db);
        var fixture = await SeedAsync(db, repository, itemCount: 1);
        var item = fixture.Items[0];
        var settings = new FakeAssignmentSettingsGateway(CurrentSettings());
        var gradebook = new FakeGradebookGateway(CurrentGradebook(), async () =>
        {
            item.ApplyTeacherReview(8m, "Revisao concorrente.", "teacher-1", 321, "approved", "motivo sintetico");
            await db.SaveChangesAsync();
        });
        var handler = CreateHandler(db, repository, settings, gradebook);

        await Assert.ThrowsAsync<GradingDraftVersionConflictException>(() => handler.Handle(
            new RefreshGradingScaleCommand(
                fixture.Batch.Id,
                501,
                [Expectation(item, fixture.Snapshots[item.Id])]),
            CancellationToken.None));

        Assert.Equal(8m, item.FinalGrade);
        Assert.Equal("Revisao concorrente.", item.FinalFeedback);
        Assert.Equal(GradingReviewStatus.Reviewed, item.ReviewStatus);
        Assert.Equal(fixture.Snapshots[item.Id].ContextHash, item.ContextHash);
        Assert.Equal(1, await db.GradingContextSnapshots.CountAsync(snapshot => snapshot.GradingItemId == item.Id));
    }

    [Fact]
    public async Task Refresh_NaoAlteraItemPublicadoOuComEscalaConflitante()
    {
        await using var db = CreateDbContext();
        var repository = new GradingReviewRepository(db);
        var fixture = await SeedAsync(db, repository, itemCount: 1);
        var item = fixture.Items[0];
        item.MarkCommitSucceeded();
        var publicationId = Guid.NewGuid();
        var action = CreatePendingPreview(fixture.Batch, item, publicationId);
        db.PendingMoodleActions.Add(action);
        db.GradingPublicationClaims.Add(new GradingPublicationClaimEntity
        {
            PublicationId = publicationId,
            PendingActionId = action.Id,
            GradingItemId = item.Id,
            ConnectionKey = "connection:test",
            AssignmentId = item.AssignmentId,
            MoodleUserId = item.MoodleUserId,
            AttemptNumber = item.AttemptNumber ?? 0,
            Status = "AwaitingConfirmation",
            ExpiresAt = action.ExpiresAt
        });
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new RefreshGradingScaleCommand(
                fixture.Batch.Id,
                501,
                [Expectation(item, fixture.Snapshots[item.Id])]),
            CancellationToken.None));

        Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus);
        Assert.Equal(fixture.Snapshots[item.Id].ContextHash, item.ContextHash);
        Assert.Equal(1, await db.GradingContextSnapshots.CountAsync(snapshot => snapshot.GradingItemId == item.Id));
        Assert.Equal(PendingActionStatus.PendingConfirmation,
            (await db.PendingMoodleActions.SingleAsync(candidate => candidate.Id == action.Id)).Status);
    }

    [Fact]
    public async Task Refresh_EscalaConflitanteNaoAlteraNenhumSnapshot()
    {
        await using var db = CreateDbContext();
        var repository = new GradingReviewRepository(db);
        var fixture = await SeedAsync(db, repository, itemCount: 1);
        var item = fixture.Items[0];
        var conflictingGradebook = CurrentGradebook() with
        {
            Items = [CurrentGradebook().Items.Single() with { GradeMax = 35m }]
        };
        var handler = CreateHandler(
            db,
            repository,
            new FakeAssignmentSettingsGateway(CurrentSettings()),
            new FakeGradebookGateway(conflictingGradebook));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new RefreshGradingScaleCommand(
                fixture.Batch.Id,
                501,
                [Expectation(item, fixture.Snapshots[item.Id])]),
            CancellationToken.None));

        Assert.Equal(fixture.Snapshots[item.Id].ContextHash, item.ContextHash);
        Assert.Equal(1, await db.GradingContextSnapshots.CountAsync(snapshot => snapshot.GradingItemId == item.Id));
        Assert.Empty(await db.MoodleAuditLogs.ToArrayAsync());
    }

    [Fact]
    public async Task Refresh_UsaSettingsFrescosQuandoLeituraSuplementarDoGradebookFalha()
    {
        await using var db = CreateDbContext();
        var repository = new GradingReviewRepository(db);
        var fixture = await SeedAsync(db, repository, itemCount: 1);
        var item = fixture.Items[0];
        var gradebook = new FakeGradebookGateway(CurrentGradebook())
        {
            ReadFailure = new InvalidOperationException("synthetic gradebook read failure")
        };
        var settings = new FakeAssignmentSettingsGateway(CurrentSettings());
        var connectionRestorer = new FakeBackgroundConnectionContextRestorer();
        var handler = CreateHandler(db, repository, settings, gradebook, connectionRestorer);

        var result = await handler.Handle(
            new RefreshGradingScaleCommand(
                fixture.Batch.Id,
                501,
                [Expectation(item, fixture.Snapshots[item.Id])]),
            CancellationToken.None);

        var latestDocument = await db.GradingContextSnapshots
            .SingleAsync(snapshot => snapshot.GradingItemId == item.Id && snapshot.Version == 2);
        var latestSnapshot = GradingContextSnapshot.DeserializeCanonicalPayload(latestDocument.PayloadJson);

        Assert.Equal("refreshed", result.Status);
        Assert.Equal(40m, latestSnapshot.GradingScale!.MaximumGrade);
        Assert.Equal("assignment_settings", latestSnapshot.GradingScale.Source);
        Assert.Equal(1, settings.FreshReadCount);
        Assert.Equal(1, gradebook.PreviewReadCount);
        Assert.Equal("connection:test", connectionRestorer.ConnectionId);
        Assert.Equal("teacher-1", connectionRestorer.Subject);
    }

    private static RefreshGradingScaleCommandHandler CreateHandler(
        ConnectorDbContext db,
        GradingReviewRepository repository,
        FakeAssignmentSettingsGateway? settings = null,
        FakeGradebookGateway? gradebook = null,
        FakeBackgroundConnectionContextRestorer? connectionRestorer = null)
    {
        var currentSettings = settings ?? new FakeAssignmentSettingsGateway(CurrentSettings());
        var currentGradebook = gradebook ?? new FakeGradebookGateway(CurrentGradebook());
        return new RefreshGradingScaleCommandHandler(
            repository,
            repository,
            currentSettings,
            currentGradebook,
            new PendingMoodleActionRepository(db),
            new MoodleAuditLogRepository(db),
            new FakeCurrentUserContext("teacher-1"),
            backgroundConnectionContextRestorer: connectionRestorer ?? new FakeBackgroundConnectionContextRestorer());
    }

    private sealed class FakeBackgroundConnectionContextRestorer : IMoodleBackgroundConnectionContextRestorer
    {
        public string? ConnectionId { get; private set; }
        public string? Subject { get; private set; }

        public Task<bool> TryRestoreAsync(
            string connectionId,
            string subject,
            CancellationToken cancellationToken)
        {
            ConnectionId = connectionId;
            Subject = subject;
            return Task.FromResult(true);
        }
    }

    private static async Task<SeedFixture> SeedAsync(
        ConnectorDbContext db,
        GradingReviewRepository repository,
        int itemCount)
    {
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            itemCount,
            moodleConnectionId: "connection:test");
        var items = Enumerable.Range(0, itemCount)
            .Select(index => AssistedGradingItem.Create(batch.Id, 10, 501, 9001 + index, 101 + index, 0))
            .ToArray();
        var snapshots = new Dictionary<Guid, GradingContextSnapshot>();
        await repository.AddBatchAsync(batch, CancellationToken.None);
        foreach (var item in items)
        {
            item.SetDraft(6m, 0.9m, "Rascunho sintetico.");
            var snapshot = CreateSnapshot(item, version: 1, scaleVersion: "old-scale-v1", maximumGrade: 10m);
            item.RecordContextSnapshot(snapshot);
            snapshots[item.Id] = snapshot;
            await repository.AddItemAsync(item, CancellationToken.None);
            await repository.PublishAsync(snapshot, CancellationToken.None);
        }

        await repository.SaveChangesAsync(CancellationToken.None);
        return new SeedFixture(batch, items, snapshots);
    }

    private static GradingContextSnapshot CreateSnapshot(
        AssistedGradingItem item,
        int version,
        string scaleVersion,
        decimal maximumGrade) =>
        GradingContextSnapshot.Create(
            item.Id,
            item.BatchId,
            new MoodleAssignmentReference(item.CourseId, item.AssignmentId, null),
            new MoodleSubmissionReference(item.SubmissionId!.Value),
            new MoodleUserReference(item.MoodleUserId),
            item.AttemptNumber,
            version,
            "Atividade sintetica",
            "Enunciado sintetico",
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

    private static GradingScaleRefreshItemExpectation Expectation(
        AssistedGradingItem item,
        GradingContextSnapshot snapshot) =>
        new(item.Id, snapshot.Version, snapshot.ContextHash, GradingDraftVersionHash.Compute(item));

    private static PendingMoodleAction CreatePendingPreview(
        AssistedGradingBatch batch,
        AssistedGradingItem item,
        Guid publicationId)
    {
        var payload = new GradingLaunchPayload(
            batch.Id,
            [new GradingLaunchPayloadItem(
                item.Id,
                item.CourseId.ToString(),
                item.AssignmentId.ToString(),
                item.MoodleUserId.ToString(),
                item.SuggestedGrade,
                item.DraftFeedback ?? string.Empty,
                item.AttemptNumber,
                GradingDraftVersionHash.Compute(item),
                item.ContextHash)],
            PublicationId: publicationId);
        return new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        };
    }

    private static AssignmentSettingsSummary CurrentSettings() =>
        new("501", 40m, "Atividade sintetica", true, RawGrade: 40m);

    private static CourseGradebook CurrentGradebook() => new(
        "10",
        "101",
        [new GradebookItem(
            Id: "501",
            ItemName: "Atividade sintetica",
            ItemType: "mod",
            ItemModule: "assign",
            CategoryId: null,
            GradeRaw: null,
            GradeFormatted: null,
            GradeMin: 0m,
            GradeMax: 40m,
            PercentageFormatted: null,
            Feedback: null,
            FeedbackFormat: null,
            GradedDateSubmitted: null,
            GradedDateGraded: null,
            GraderId: null,
            ItemInstance: "501",
            CourseModuleId: "901",
            ScaleId: 0,
            GradeType: 1)]);

    private static ConnectorDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"scale-refresh-{Guid.NewGuid():N}")
            .Options;
        return new ConnectorDbContext(options);
    }

    private sealed record SeedFixture(
        AssistedGradingBatch Batch,
        IReadOnlyList<AssistedGradingItem> Items,
        IReadOnlyDictionary<Guid, GradingContextSnapshot> Snapshots);

    private sealed class FakeAssignmentSettingsGateway(AssignmentSettingsSummary settings)
        : IMoodleAssignmentSettingsGateway
    {
        public int FreshReadCount { get; private set; }

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken) => Task.FromResult<AssignmentSettingsSummary?>(settings);

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsFreshAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken)
        {
            FreshReadCount++;
            return Task.FromResult<AssignmentSettingsSummary?>(settings);
        }
    }

    private sealed class FakeGradebookGateway(
        CourseGradebook gradebookResult,
        Func<Task>? afterRead = null) : IMoodleGradebookGateway
    {
        public int PreviewReadCount { get; private set; }
        public Exception? ReadFailure { get; set; }

        public async Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            GradebookReadOptions readOptions,
            CancellationToken cancellationToken)
        {
            Assert.Equal(GradebookReadOptions.Preview, readOptions);
            PreviewReadCount++;
            if (ReadFailure is not null)
            {
                return await Task.FromException<CourseGradebook>(ReadFailure);
            }

            if (afterRead is not null)
            {
                await afterRead();
            }

            return gradebookResult with { CourseId = courseId, StudentId = studentId };
        }

        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(gradebookResult with { CourseId = courseId, StudentId = studentId });
    }

    private sealed class FakeCurrentUserContext(string subject) : ICurrentUserContext
    {
        public string Subject { get; } = subject;
        public string? Email => "teacher@example.test";
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
        public bool HasPlatformPermission(string permission) => permission == "tool.assignments.grade";
    }
}
