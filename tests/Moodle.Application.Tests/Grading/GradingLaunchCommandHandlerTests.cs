using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Application.PendingActions;
using Moodle.Application.Tools;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingLaunchCommandHandlerTests
{
    [Fact]
    public async Task CreatePreview_CriaPendingActionComItensRevisados()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(
                batch.Id,
                GradingItemIds: [],
                OnlyReviewed: true),
            CancellationToken.None);

        Assert.True(
            fixture.PendingActions.PendingActionId == result.PendingActionId,
            $"pending={result.PendingActionId}; ready={result.ReadyItems}; blocked={result.BlockedItems}; reasons={JsonSerializer.Serialize(result.BlockedReasons)}; warnings={string.Join(" | ", result.Warnings)}");
        Assert.Equal(batch.Id, result.BatchJobId);
        Assert.Equal(1, result.ReadyItems);
        Assert.Equal(0, result.BlockedItems);
        Assert.Empty(result.ConfirmationText);
        Assert.NotNull(fixture.PendingActions.LastPayload);
        Assert.Single(fixture.PendingActions.LastPayload!.Items);
        Assert.Equal("501", fixture.PendingActions.LastPayload.Items[0].AssignmentId);
        Assert.Equal("101", fixture.PendingActions.LastPayload.Items[0].StudentId);
        Assert.Equal(8.5m, fixture.PendingActions.LastPayload.Items[0].Grade);
        Assert.Equal("Feedback final revisado.", fixture.PendingActions.LastPayload.Items[0].FeedbackText);
        Assert.False(string.IsNullOrWhiteSpace(fixture.PendingActions.LastPayload.Items[0].DraftVersionHash));
        Assert.Equal(fixture.GradingRepository.Items.Single().ContextHash, fixture.PendingActions.LastPayload.Items[0].ContextHash);
    }

    [Fact]
    public async Task CreatePreview_RestauraConexaoDoLoteAntesDeResolverEscala()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            connectorClientId: "client-fieg",
            connectionAlias: "fieg");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321, "approved", "ok");
        AttachVersionedContext(item, batch);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(item);

        var selection = new FakeMoodleConnectionSelection();
        var executionContext = new FakeConnectorExecutionContext();
        var settings = new ConnectionScopedSettingsGateway(selection);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            settings,
            gradeReadGateway: fixture.ExistingGrades,
            executionContext: executionContext,
            connectionSelection: selection,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(1, result.ReadyItems);
        Assert.Equal(0, result.BlockedItems);
        Assert.Equal("fieg", selection.Alias);
        Assert.Equal("client-fieg", executionContext.ClientId);
        Assert.Equal("teacher-1", executionContext.Subject);
        Assert.Equal("assignment_settings", Assert.Single(result.Launches).ScaleSource);
    }

    [Fact]
    public async Task CreatePreview_UsaEscalaDoItemDeGradebookExatoQuandoSettingsNaoClassificamModo()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        fixture.SettingsGateway.Result = new AssignmentSettingsSummary(
            "501",
            0m,
            "Atividade",
            IsGradable: null,
            RawGrade: null,
            MarkingWorkflowEnabled: false);
        var gradebook = new FakeMoodleGradebookGateway(new CourseGradebook("10", "101",
        [
            new GradebookItem(
                "grade-item-77",
                "Atividade",
                "mod",
                "assign",
                null,
                null,
                null,
                2m,
                20m,
                null,
                null,
                null,
                null,
                null,
                null,
                "501",
                "990",
                ScaleId: 0,
                GradeType: 1,
                FeedbackFieldPresent: true)
        ]));
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: gradebook);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        var launch = Assert.Single(result.Launches);
        Assert.Equal(1, result.ReadyItems);
        Assert.Equal(2m, launch.MinGrade);
        Assert.Equal(20m, launch.MaxGrade);
        Assert.Equal("numeric", launch.GradingMode);
        Assert.Equal("activity_gradebook", launch.ScaleSource);
        Assert.False(string.IsNullOrWhiteSpace(launch.ScaleVersion));
        Assert.Equal(GradebookReadOptions.FreshBatch, gradebook.LastReadOptions);
    }

    [Fact]
    public async Task CreatePreview_BloqueiaQuandoSettingsEItemExatoDeGradebookDivergem()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var gradebook = CreateGradebookGateway(maximumGrade: 20m);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: gradebook);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(0, result.ReadyItems);
        Assert.Equal(1, result.BlockedItems);
        Assert.Contains(result.Warnings, warning => warning.Contains("escala", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GradebookReadOptions.FreshBatch, gradebook.LastReadOptions);
        Assert.Equal(1, gradebook.ReadCalls);
    }

    [Fact]
    public async Task CreatePreview_UsaSettingsFrescosQuandoLeituraSuplementarDoGradebookFalha()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var gradebook = CreateGradebookGateway(maximumGrade: 10m);
        gradebook.ReadFailure = new InvalidOperationException("synthetic gradebook read failure");
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: gradebook);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(0, result.ReadyItems);
        Assert.Equal(1, result.BlockedItems);
        Assert.Contains("feedback_read_failed", result.BlockedReasons.Keys);
        Assert.Equal(GradebookReadOptions.FreshBatch, gradebook.LastReadOptions);
    }

    [Fact]
    public async Task CreatePreview_BloqueiaFalhaDoGradebookQuandoSettingsNaoDefinemEscala()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        fixture.SettingsGateway.Result = null;
        var gradebook = CreateGradebookGateway(maximumGrade: 10m);
        gradebook.ReadFailure = new InvalidOperationException("synthetic gradebook read failure");
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: gradebook);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(0, result.ReadyItems);
        Assert.Equal(1, result.BlockedItems);
        Assert.Equal(1, result.BlockedItems);
    }

    [Fact]
    public void GradingItem_RetrySeguroEAgendadoComOrcamentoPersistivel()
    {
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final.", "teacher-1", 321, "approved", null);
        var now = DateTimeOffset.UtcNow;
        var firstRetryAt = now.AddSeconds(5);

        Assert.True(item.ScheduleCommitRetry("Moodle confirmou nao aplicacao.", "moodle_write_reconciled_not_applied", firstRetryAt));
        Assert.Equal(GradingCommitStatus.RetryScheduled, item.CommitStatus);
        Assert.Equal(1, item.CommitRetryCount);
        Assert.Equal(firstRetryAt, item.CommitNextAttemptAt);
        Assert.False(item.BeginScheduledCommitRetry(now.AddSeconds(4)));
        Assert.True(item.BeginScheduledCommitRetry(firstRetryAt));
        Assert.Equal(GradingCommitStatus.Pending, item.CommitStatus);

        var secondRetryAt = firstRetryAt.AddSeconds(20);
        Assert.True(item.ScheduleCommitRetry("Nova nao aplicacao confirmada.", "moodle_write_reconciled_not_applied", secondRetryAt));
        Assert.Equal(2, item.CommitRetryCount);
        Assert.False(item.ScheduleCommitRetry("Orcamento esgotado.", "moodle_write_reconciled_not_applied", secondRetryAt.AddSeconds(20)));
    }

    [Fact]
    public void GradingItem_PersisteIntencaoDeEscritaEAMantemAteResultadoConhecido()
    {
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final.", "teacher-1", 321, "approved", null);
        var intentAt = DateTimeOffset.UtcNow;

        Assert.True(item.TryMarkCommitWriteIntent(intentAt));
        Assert.Equal(intentAt, item.CommitWriteIntentAt);
        Assert.False(item.TryMarkCommitWriteIntent(intentAt.AddSeconds(1)));

        item.MarkCommitExecutionUnknown("Resposta perdida.");
        Assert.Equal(intentAt, item.CommitWriteIntentAt);

        item.MarkCommitSucceeded();
        Assert.Null(item.CommitWriteIntentAt);
    }

    [Fact]
    public void GradingItem_LimpaIntencaoSomenteDepoisDeNaoAplicacaoConfirmada()
    {
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final.", "teacher-1", 321, "approved", null);
        item.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow);

        Assert.True(item.ScheduleCommitRetry(
            "Moodle confirmou nao aplicacao.",
            "moodle_write_reconciled_not_applied",
            DateTimeOffset.UtcNow.AddSeconds(5)));

        Assert.Null(item.CommitWriteIntentAt);
    }

    [Fact]
    public void GradingDraftVersionHash_IgnoraTransicoesOperacionaisMasDetectaMudancaNaNota()
    {
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final.", "teacher-1", 321, "approved", null);
        var approvedDraftHash = GradingDraftVersionHash.Compute(item);

        Assert.True(item.ScheduleCommitRetry(
            "Moodle confirmou nao aplicacao.",
            "moodle_write_reconciled_not_applied",
            DateTimeOffset.UtcNow.AddSeconds(5)));
        Assert.Equal(approvedDraftHash, GradingDraftVersionHash.Compute(item));
        Assert.True(item.BeginScheduledCommitRetry(DateTimeOffset.UtcNow.AddSeconds(6)));
        Assert.Equal(approvedDraftHash, GradingDraftVersionHash.Compute(item));

        item.ApplyTeacherReview(7.5m, "Feedback revisado.", "teacher-1", 321, "changed_after_preview", null);
        Assert.NotEqual(approvedDraftHash, GradingDraftVersionHash.Compute(item));
    }

    [Fact]
    public void GradingDraftVersionHash_PromocaoDaPropostaAprovadaPreservaVersaoDaPrevia()
    {
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8.5m, 0.8m, "Feedback aprovado.");
        var previewHash = GradingDraftVersionHash.Compute(item);

        item.ApplyTeacherReview(8.5m, "Feedback aprovado.", "teacher-1", 321, "confirmed_for_publication");
        Assert.Equal(previewHash, GradingDraftVersionHash.Compute(item));

        item.ApplyTeacherReview(7.5m, "Feedback alterado.", "teacher-1", 321, "changed_after_preview");
        Assert.NotEqual(previewHash, GradingDraftVersionHash.Compute(item));
    }

    [Fact]
    public async Task CreatePreview_RepeteMesmoEscopoEReutilizaPreviaAtiva()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var firstHandler = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);
        var first = await firstHandler.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);
        var action = new PendingMoodleAction
        {
            Id = first.PendingActionId,
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = Moodle.Application.Auditing.AuditPayloadSanitizer.SerializeSanitized(
                fixture.PendingActions.LastPayload),
            PreviewJson = Moodle.Application.Auditing.AuditPayloadSanitizer.SerializeSanitized(
                fixture.PendingActions.LastPreview),
            ConfirmationText = string.Empty,
            ExpiresAt = first.ExpiresAt!.Value
        };
        fixture.PendingRepository.Actions.Add(action);

        var retryHandler = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            pendingActionRepository: fixture.PendingRepository,
            gradebookGateway: fixture.GradebookGateway);
        var retried = await retryHandler.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(first.PendingActionId, retried.PendingActionId);
        Assert.Equal("awaiting_approval", retried.PublicationState);
        Assert.Equal(1, fixture.PendingActions.CreateCallCount);
        Assert.Equal(first.Launches, retried.Launches);
    }

    [Fact]
    public async Task CreatePreview_ReutilizaPreviaLegadaQuandoConteudoEMetadadosContinuamIguais()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var firstHandler = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);
        var first = await firstHandler.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);
        var item = fixture.GradingRepository.Items.Single();
        var legacyPayload = fixture.PendingActions.LastPayload! with
        {
            Items = fixture.PendingActions.LastPayload!.Items
                .Select(payloadItem => payloadItem with
                {
                    DraftVersionHash = GradingDraftVersionHash.ComputeLegacy(item)
                })
                .ToArray()
        };
        fixture.PendingRepository.Actions.Add(new PendingMoodleAction
        {
            Id = first.PendingActionId,
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = Moodle.Application.Auditing.AuditPayloadSanitizer.SerializeSanitized(legacyPayload),
            PreviewJson = Moodle.Application.Auditing.AuditPayloadSanitizer.SerializeSanitized(
                fixture.PendingActions.LastPreview),
            ConfirmationText = string.Empty,
            ExpiresAt = first.ExpiresAt!.Value
        });

        var retryHandler = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            pendingActionRepository: fixture.PendingRepository,
            gradebookGateway: fixture.GradebookGateway);
        var retried = await retryHandler.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(first.PendingActionId, retried.PendingActionId);
        Assert.Equal("awaiting_approval", retried.PublicationState);
        Assert.Equal(1, fixture.PendingActions.CreateCallCount);
    }

    [Fact]
    public async Task GetPublicationStatus_ByRunRecuperaPreviaPaginadaEFeedbackPersistidos()
    {
        var fixture = new Fixture();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1, gradingRunId: run.Id);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback integral recuperado.", "teacher-1", 321, "approved", null);
        fixture.GradingRepository.Runs.Add(run);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(item);
        var payloadItem = new GradingLaunchPayloadItem(
            item.Id, "10", "501", "101", 8.5m, "Feedback integral recuperado.", 0, "draft-hash",
            StudentName: "Aluno de teste", AssignmentName: "Atividade 01", MaxGrade: 10m);
        var payload = new GradingLaunchPayload(batch.Id, [payloadItem]);
        var launch = new GradingLaunchPreviewItem(
            item.Id, "501", "101", 8.5m, "Feedback integral recuperado.",
            StudentName: "Aluno de teste", AssignmentName: "Atividade 01", MaxGrade: 10m, ReviewStatus: "Reviewed");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = JsonSerializer.Serialize(payload, options),
            PreviewJson = JsonSerializer.Serialize(new
            {
                batchJobId = batch.Id,
                totalItems = 1,
                readyItems = 1,
                blockedItems = 0,
                launches = new[] { launch },
                warnings = new[] { "Aviso persistido." }
            }, options),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        action.Authorize("teacher-1", DateTimeOffset.UtcNow);
        fixture.PendingRepository.Actions.Add(action);

        var sut = new GetGradingPublicationStatusQueryHandler(
            fixture.GradingRepository,
            fixture.PendingRepository,
            fixture.CurrentUser);
        var result = await sut.Handle(
            new GetGradingPublicationStatusQuery(GradingRunId: run.Id, Page: 1, PageSize: 1),
            CancellationToken.None);

        var recovered = Assert.Single(result.Items);
        Assert.Equal(action.Id, result.PendingActionId);
        Assert.Equal("queued", result.PublicationState);
        Assert.Equal("Feedback integral recuperado.", recovered.FeedbackText);
        Assert.Equal("queued", recovered.PublicationState);
        Assert.Equal("Pending", recovered.CommitStatus);
        Assert.Equal("Atividade 01", recovered.AssignmentName);
        Assert.Contains("Aviso persistido.", result.Warnings);
        Assert.False(result.HasMore);
        Assert.Equal(1, result.PublicationCounts.SelectedItems);
        Assert.Equal(1, result.PublicationCounts.Queued);
        Assert.Equal(0, result.PublicationCounts.Succeeded);
        Assert.Equal("wait", result.NextAction);

        // A terminal action aggregate is not evidence that this item was
        // written. The item ledger remains the source of success truth.
        action.MarkExecuted();
        var inconsistent = await sut.Handle(
            new GetGradingPublicationStatusQuery(PendingActionId: action.Id),
            CancellationToken.None);
        Assert.Equal("unknown", inconsistent.PublicationState);
        Assert.Equal("unknown", Assert.Single(inconsistent.Items).PublicationState);
        Assert.Equal("publication_item_state_inconsistent", inconsistent.Items[0].FailureReason);
        Assert.Equal(1, inconsistent.PublicationCounts.Unknown);
        Assert.Equal("attention", inconsistent.NextAction);
    }

    [Fact]
    public async Task GetPublicationStatus_UsaResultadoDoClaimDaAcaoEnaoLedgerDeOutraPublicacao()
    {
        var fixture = new Fixture();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1, gradingRunId: run.Id);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback da publicacao atual.", "teacher-1", 321, "approved", null);
        item.MarkCommitSucceeded();
        fixture.GradingRepository.Runs.Add(run);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(item);

        var publicationId = Guid.NewGuid();
        var payloadItem = new GradingLaunchPayloadItem(
            item.Id, "10", "501", "101", 8.5m, "Feedback da publicacao atual.", 0, "draft-hash");
        var payload = new GradingLaunchPayload(
            batch.Id,
            [payloadItem],
            PublicationId: publicationId);
        var launch = new GradingLaunchPreviewItem(
            item.Id, "501", "101", 8.5m, "Feedback da publicacao atual.", ReviewStatus: "Reviewed");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = JsonSerializer.Serialize(payload, options),
            PreviewJson = JsonSerializer.Serialize(new
            {
                batchJobId = batch.Id,
                totalItems = 1,
                readyItems = 1,
                blockedItems = 0,
                launches = new[] { launch },
                warnings = Array.Empty<string>()
            }, options),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        action.MarkExecuted();
        fixture.PendingRepository.Actions.Add(action);
        fixture.GradingRepository.PublicationClaimStatuses[publicationId] =
            new Dictionary<Guid, string> { [item.Id] = "Failed" };

        var sut = new GetGradingPublicationStatusQueryHandler(
            fixture.GradingRepository,
            fixture.PendingRepository,
            fixture.CurrentUser);
        var result = await sut.Handle(
            new GetGradingPublicationStatusQuery(PendingActionId: action.Id),
            CancellationToken.None);

        Assert.Equal("failed", result.PublicationState);
        Assert.Equal("failed", Assert.Single(result.Items).PublicationState);

        fixture.GradingRepository.PublicationClaimStatuses[publicationId] =
            new Dictionary<Guid, string> { [item.Id] = "Authorized" };
        var staleTerminal = await sut.Handle(
            new GetGradingPublicationStatusQuery(PendingActionId: action.Id),
            CancellationToken.None);
        Assert.Equal("unknown", staleTerminal.PublicationState);
        Assert.Equal("unknown", Assert.Single(staleTerminal.Items).PublicationState);
    }

    [Fact]
    public async Task CreatePreviewDoRun_BloqueiaPublicacaoGlobalQuandoCoberturaEstaIncompleta()
    {
        var fixture = new Fixture();
        var run = GradingRun.Create("teacher-1");
        run.SetExpectedCoverage(expectedItemCount: 2, expectedBatchCount: 2);
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            gradingRunId: run.Id);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321, "approved", "ok");
        AttachVersionedContext(item, batch);
        fixture.GradingRepository.Runs.Add(run);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(item);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(run.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(Guid.Empty, result.PendingActionId);
        Assert.Equal(2, result.ExpectedItems);
        Assert.Equal(1, result.PreparedItems);
        Assert.Equal(1, result.MissingItems);
        Assert.Equal(1, result.MissingBatchCount);
        Assert.False(result.DecisionSafe);
        Assert.Null(fixture.PendingActions.LastPayload);
        Assert.Contains(result.Warnings, warning => warning.Contains("allowPartial=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatePreviewDoRun_ComAvisoTecnicoDeCoberturaMantemItensPreparados()
    {
        var fixture = new Fixture();
        var run = GradingRun.Create("teacher-1");
        run.SetExpectedCoverage(expectedItemCount: 2, expectedBatchCount: 2);
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            gradingRunId: run.Id);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321, "approved", "ok");
        AttachVersionedContext(item, batch);
        fixture.GradingRepository.Runs.Add(run);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(item);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingSecurityWarningsOnly = true
            }),
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(run.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.PendingActionId);
        Assert.Equal(1, result.ReadyItems);
        Assert.Equal(1, result.PreparedItems);
        Assert.Equal(1, result.MissingItems);
        Assert.False(result.DecisionSafe);
        Assert.Contains(result.Warnings, warning => warning.Contains("Aviso tecnico de cobertura", StringComparison.Ordinal));
        Assert.Single(fixture.PendingActions.LastPayload!.Items);
    }

    [Fact]
    public async Task CreatePreview_BloqueiaFeedbackReutilizadoNaMesmaAtividade()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var first = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var second = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        first.SetDraft(8m, 0.8m, "Rascunho 1.");
        second.SetDraft(7m, 0.8m, "Rascunho 2.");
        first.ApplyTeacherReview(8m, "Feedback individual reutilizado.", "teacher-1", 321, "approved", "ok");
        second.ApplyTeacherReview(7m, "Feedback individual reutilizado.", "teacher-1", 321, "approved", "ok");
        AttachVersionedContext(first, batch);
        AttachVersionedContext(second, batch);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(first);
        fixture.GradingRepository.Items.Add(second);

        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(Guid.Empty, result.PendingActionId);
        Assert.Equal(0, result.ReadyItems);
        Assert.Equal(2, result.BlockedItems);
        Assert.Null(fixture.PendingActions.LastPayload);
        Assert.Contains(result.Warnings, warning => warning.Contains("feedback reutilizado", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreatePreview_BloqueiaItemSemRevisao()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        await fixture.GradingRepository.AddBatchAsync(batch, CancellationToken.None);
        await fixture.GradingRepository.AddItemAsync(item, CancellationToken.None);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(
                batch.Id,
                GradingItemIds: [],
                OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(Guid.Empty, result.PendingActionId);
        Assert.Equal(0, result.ReadyItems);
        Assert.Equal(1, result.BlockedItems);
        Assert.Null(fixture.PendingActions.LastPayload);
        Assert.Contains(result.Warnings, warning => warning.Contains("Nenhuma correcao salva", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreatePreview_IncluiRascunhoSalvoSemExigirUiDeRevisao()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0, "Ana Silva");
        item.SetDraft(8m, 0.8m, "Bom trabalho.");
        AttachVersionedContext(item, batch);
        await fixture.GradingRepository.AddBatchAsync(batch, CancellationToken.None);
        await fixture.GradingRepository.AddItemAsync(item, CancellationToken.None);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: false),
            CancellationToken.None);

        Assert.Empty(result.ConfirmationText);
        var preview = Assert.Single(result.Launches);
        Assert.Equal("Ana Silva", preview.StudentName);
        Assert.Equal(8m, preview.Grade);
        Assert.Equal("Bom trabalho.", preview.FeedbackText);
        Assert.Equal("rascunho_aguardando_confirmacao", preview.Situation);
        Assert.Equal(GradingReviewStatus.NotReviewed, item.ReviewStatus);
        Assert.Equal(GradingItemStatus.DraftReady, item.Status);
    }

    [Fact]
    public async Task CreatePreview_EnriqueceContratoComNomeAtividadeEscalaENomeDoAluno()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            participantsGateway: fixture.EnrollmentGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        var preview = Assert.Single(result.Launches);
        Assert.Equal("Student 101", preview.StudentName);
        Assert.Equal("Atividade", preview.AssignmentName);
        Assert.Equal(10m, preview.MaxGrade);
        Assert.Equal("Reviewed", preview.ReviewStatus);
        Assert.Equal("Student 101", fixture.PendingActions.LastPayload!.Items[0].StudentName);
        Assert.Equal("Atividade", fixture.PendingActions.LastPayload.Items[0].AssignmentName);
        Assert.Equal(10m, fixture.PendingActions.LastPayload.Items[0].MaxGrade);
    }

    [Fact]
    public async Task CreatePreview_ExplicaQuandoContextoAtualBloqueadoNaoFoiSelado()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        AttachBlockedVersionedContext(item, batch);
        await fixture.GradingRepository.AddBatchAsync(batch, CancellationToken.None);
        await fixture.GradingRepository.AddItemAsync(item, CancellationToken.None);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: false),
            CancellationToken.None);

        Assert.Equal(0, result.ReadyItems);
        Assert.Equal(1, result.BlockedItems);
        Assert.Contains(result.Warnings, warning => warning.Contains("nao foi selado", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("ausente ou legado", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreatePreview_ComAvisoTecnicoDeIntegridadeMantemItemPreparado()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        AttachBlockedVersionedContext(item, batch);
        await fixture.GradingRepository.AddBatchAsync(batch, CancellationToken.None);
        await fixture.GradingRepository.AddItemAsync(item, CancellationToken.None);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingSecurityWarningsOnly = true
            }),
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: false),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.PendingActionId);
        Assert.Equal(1, result.ReadyItems);
        Assert.Equal(0, result.BlockedItems);
        Assert.Contains(result.Warnings, warning => warning.Contains("aviso tecnico de seguranca", StringComparison.OrdinalIgnoreCase));
        Assert.Single(fixture.PendingActions.LastPayload!.Items);
    }

    [Fact]
    public async Task CreatePreview_AceitaContextoAtualBloqueadoQuandoRascunhoFoiSelado()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        AttachBlockedVersionedContext(item, batch);
        item.RecordSubmissionIntegrity(new string('a', 64), [new string('b', 32)]);
        await fixture.GradingRepository.AddBatchAsync(batch, CancellationToken.None);
        await fixture.GradingRepository.AddItemAsync(item, CancellationToken.None);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: false),
            CancellationToken.None);

        Assert.Equal(1, result.ReadyItems);
        Assert.Equal(0, result.BlockedItems);
        Assert.Equal(new string('a', 64), Assert.Single(result.Launches).SubmissionContentHash);
    }

    [Fact]
    public async Task CreatePreview_PermiteLancamentoSomenteFeedback()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(null, 0.8m, "Rascunho de feedback.");
        item.ApplyTeacherReview(null, "Feedback final sem nota.", "teacher-1", 321, "approved", "atividade nao avaliativa");
        AttachVersionedContext(item, batch);
        await fixture.GradingRepository.AddBatchAsync(batch, CancellationToken.None);
        await fixture.GradingRepository.AddItemAsync(item, CancellationToken.None);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.PendingActionId);
        Assert.Equal(1, result.ReadyItems);
        Assert.Empty(result.ConfirmationText);
        Assert.Null(Assert.Single(fixture.PendingActions.LastPayload!.Items).Grade);
    }

    [Fact]
    public async Task CreatePreview_RestauraContextoPersistidoDeItemCriadoDuranteRollout()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(null, 0.8m, "Rascunho de feedback.");
        item.ApplyTeacherReview(null, "Feedback final sem nota.", "teacher-1", 321, "approved", null);
        var snapshot = AttachVersionedContext(item, batch, attachToItem: false);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(item);
        fixture.GradingRepository.Snapshots.Add(GradingContextSnapshotDocument.FromSnapshot(snapshot));
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(1, result.ReadyItems);
        Assert.Empty(result.ConfirmationText);
        Assert.Equal(snapshot.ContextHash, item.ContextHash);
        Assert.Equal(snapshot.Version, item.ContextVersion);
        Assert.Null(Assert.Single(fixture.PendingActions.LastPayload!.Items).Grade);
    }

    [Fact]
    public async Task CreatePreview_BloqueiaNotaFinalAcimaDaNotaMaximaDasEvidencias()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(12m, "Feedback final revisado.", "teacher-1", 321, "approved", "ok");
        AttachVersionedContext(item, batch);
        await fixture.GradingRepository.AddBatchAsync(batch, CancellationToken.None);
        await fixture.GradingRepository.AddItemAsync(item, CancellationToken.None);
        await fixture.GradingRepository.AddEvidenceAsync(
            new GradingEvidence(
                Guid.NewGuid(),
                item.Id,
                "c1",
                "Criterio 1",
                MaxPoints: 5m,
                SuggestedPoints: 4m,
                EvidenceText: "Evidencia.",
                GapsText: null,
                TeacherReviewRequired: false,
                CreatedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);
        await fixture.GradingRepository.AddEvidenceAsync(
            new GradingEvidence(
                Guid.NewGuid(),
                item.Id,
                "c2",
                "Criterio 2",
                MaxPoints: 5m,
                SuggestedPoints: 4m,
                EvidenceText: "Evidencia.",
                GapsText: null,
                TeacherReviewRequired: false,
                CreatedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(
                batch.Id,
                GradingItemIds: [],
                OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(Guid.Empty, result.PendingActionId);
        Assert.Equal(0, result.ReadyItems);
        Assert.Equal(1, result.BlockedItems);
        Assert.Null(fixture.PendingActions.LastPayload);
        Assert.Contains(result.Warnings, warning => warning.Contains("nota 12", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("nota maxima 10", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreatePreview_BloqueiaItemLegadoSemContextoVersionado()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321, "approved", "ok");
        await fixture.GradingRepository.AddBatchAsync(batch, CancellationToken.None);
        await fixture.GradingRepository.AddItemAsync(item, CancellationToken.None);

        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
            CancellationToken.None);

        Assert.Equal(Guid.Empty, result.PendingActionId);
        Assert.Equal(0, result.ReadyItems);
        Assert.Equal(1, result.BlockedItems);
        Assert.Null(fixture.PendingActions.LastPayload);
        Assert.Contains(result.Warnings, warning => warning.Contains("contexto de correcao", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreatePreview_DeOutroCriadorSemEscopoAdmin_DeveFalhar()
    {
        var fixture = new Fixture(currentUserSubject: "teacher-2");
        var batch = fixture.CreateBatchWithReviewedItem();
        var sut = new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            gradeReadGateway: fixture.ExistingGrades,
            gradebookGateway: fixture.GradebookGateway);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sut.Handle(
                new CreateGradingLaunchPreviewCommand(
                    batch.Id,
                    GradingItemIds: [],
                    OnlyReviewed: true),
                CancellationToken.None));

        Assert.Equal("Usuario atual nao esta autorizado a acessar este lote de correcao.", ex.Message);
        Assert.Null(fixture.PendingActions.LastPayload);
    }

    [Fact]
    public async Task ConfirmLaunch_ConfirmaAcaoEEnviaNotasIndividuais()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            telemetry: fixture.Telemetry,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Equal("501", fixture.Mediator.SavedGrades[0].AssignmentId);
        Assert.Equal("101", fixture.Mediator.SavedGrades[0].StudentId);
        Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus);
        var publicationItem = Assert.Single(result.Items!);
        Assert.Equal("publicado", publicationItem.Status);
        Assert.Equal(8.5m, publicationItem.PublishedGrade);
        Assert.Equal("Feedback final revisado.", publicationItem.PublishedFeedback);
        Assert.Equal("moodle.write.assignments.grade", fixture.Confirmations.LastRequiredScope);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_succeeded");
        Assert.Equal("confirmar_lancamento_lote_moodle", auditLog.ToolName);
        Assert.Equal("mod_assign_save_grade", auditLog.MoodleFunction);
        Assert.Equal("audit-1", auditLog.CorrelationId);
        Assert.Contains(item.Id.ToString(), auditLog.RequestSanitizedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaSemAutorizarSeEscalaMudarDepoisDaPrevia()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.SettingsGateway.Result = fixture.SettingsGateway.Result! with
        {
            MaxGrade = 12m,
            RawGrade = 12m
        };
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, pendingAction.ConfirmationText),
            CancellationToken.None);

        Assert.Equal("scale_changed", result.Status);
        Assert.Equal(PendingActionStatus.Failed, pendingAction.Status);
        Assert.Equal(0, fixture.Confirmations.CallCount);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Pending, item.CommitStatus);
        Assert.Contains(result.Warnings!, warning => warning.Contains("nova prévia", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaSeEscalaNaoPuderSerConfirmadaFresca()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.SettingsGateway.Result = null;
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, pendingAction.ConfirmationText),
            CancellationToken.None);

        Assert.Equal("scale_changed", result.Status);
        Assert.Equal(PendingActionStatus.Failed, pendingAction.Status);
        Assert.Equal(0, fixture.Confirmations.CallCount);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Pending, item.CommitStatus);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoFontesAtuaisDeEscalaDivergem()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var gradebook = CreateGradebookGateway(maximumGrade: 20m);
        fixture.GradebookGateway = gradebook;
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, pendingAction.ConfirmationText),
            CancellationToken.None);

        Assert.Equal("scale_changed", result.Status);
        Assert.Equal(PendingActionStatus.Failed, pendingAction.Status);
        Assert.Equal(0, fixture.Confirmations.CallCount);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Pending, item.CommitStatus);
        Assert.Equal(GradebookReadOptions.Preview, gradebook.LastReadOptions);
    }

    [Fact]
    public async Task ConfirmLaunch_UsaSettingsFrescosQuandoLeituraSuplementarDoGradebookFalha()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var gradebook = CreateGradebookGateway(maximumGrade: 10m);
        gradebook.ReadFailure = new InvalidOperationException("synthetic gradebook read failure");
        fixture.GradebookGateway = gradebook;
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, pendingAction.ConfirmationText),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal(1, fixture.Confirmations.CallCount);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(1, result.FailedItems);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Equal(GradebookReadOptions.FreshBatch, gradebook.LastReadOptions);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaFalhaDoGradebookQuandoSettingsNaoDefinemEscala()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.SettingsGateway.Result = null;
        var gradebook = CreateGradebookGateway(maximumGrade: 10m);
        gradebook.ReadFailure = new InvalidOperationException("synthetic gradebook read failure");
        fixture.GradebookGateway = gradebook;
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, pendingAction.ConfirmationText),
            CancellationToken.None);

        Assert.Equal("scale_changed", result.Status);
        Assert.Equal(0, fixture.Confirmations.CallCount);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Pending, item.CommitStatus);
        Assert.Equal(GradebookReadOptions.Preview, gradebook.LastReadOptions);
    }

    [Fact]
    public async Task ConfirmLaunch_RejeitaConexaoDivergenteAntesDeAutorizarAcao()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(
            batch.Id,
            item.Id,
            connectionKey: "client:preview-client:alias:preview-alias");
        fixture.PendingRepository.Actions.Add(pendingAction);
        var credentials = new FakeMoodleCredentialsProvider(new MoodleCredentials(
            ClientId: "current-client",
            ConnectionId: "connection-current",
            Alias: "current-alias",
            BaseUrl: "https://moodle.example",
            Username: "teacher",
            Password: "secret",
            MoodleTarget: "moodle",
            CanWrite: true));
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            credentialsProvider: credentials,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(
                new ConfirmMoodleBatchLaunchCommand(
                    pendingAction.Id,
                    "CONFIRMAR LANCAMENTO 1 ITEM"),
                CancellationToken.None));

        Assert.Equal(0, fixture.Confirmations.CallCount);
        Assert.Equal(PendingActionStatus.PendingConfirmation, pendingAction.Status);
    }

    [Fact]
    public async Task ConfirmLaunch_RestauraConexaoCapturadaAntesDeValidarPrevia()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            connectorClientId: "preview-client",
            connectionAlias: "preview-alias");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321, "approved", "ok");
        AttachVersionedContext(item, batch);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(item);
        var pendingAction = fixture.CreatePendingLaunchAction(
            batch.Id,
            item.Id,
            connectionKey: "client:preview-client:alias:preview-alias");
        fixture.PendingRepository.Actions.Add(pendingAction);

        var selection = new FakeMoodleConnectionSelection();
        var executionContext = new FakeConnectorExecutionContext();
        var credentials = new ContextMoodleCredentialsProvider(selection, executionContext);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            credentialsProvider: credentials,
            assignmentSettingsGateway: fixture.SettingsGateway,
            currentUser: fixture.CurrentUser,
            executionContext: executionContext,
            connectionSelection: selection,
            gradebookGateway: fixture.GradebookGateway);

        await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, pendingAction.ConfirmationText),
            CancellationToken.None);

        Assert.Equal(1, fixture.Confirmations.CallCount);
        Assert.Equal("preview-client", executionContext.ClientId);
        Assert.Equal("teacher-1", executionContext.Subject);
        Assert.Equal("preview-alias", selection.Alias);
    }

    [Fact]
    public async Task ConfirmLaunch_ValidaSubmissaoPorArquivoSemExigirResourceIdLocal()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var sealedHash = new string('a', 64);
        item.RecordSubmissionIntegrity(sealedHash, []);
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var resolver = new FakeSubmissionContentHashResolver(sealedHash);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            submissionContentHashResolver: resolver,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingWriteEnabled = true,
                McpGradingSecurityWarningsOnly = false
            }),
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Empty(resolver.LastAttachmentHashes!);
    }

    [Fact]
    public async Task ConfirmLaunch_ComRevisaoLeveAtualNaoBaixaOsAnexosNovamente()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var current = new AssignmentSubmissionRecord(
            "9001",
            "101",
            "submitted",
            null,
            DateTimeOffset.Parse("2026-09-23T17:00:00Z"),
            DateTimeOffset.Parse("2026-09-23T17:00:00Z"),
            0,
            0,
            false,
            []);
        fixture.AssignmentSubmissions.Submissions.Add(current);
        item.RecordSubmissionIntegrity(
            new string('a', 64),
            [],
            SubmissionRevisionHash.Compute(current));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var resolver = new FakeSubmissionContentHashResolver(new string('b', 64));
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            submissionContentHashResolver: resolver,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingWriteEnabled = true,
                McpGradingSecurityWarningsOnly = false
            }),
            submissionsGateway: fixture.AssignmentSubmissions,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(1, fixture.AssignmentSubmissions.BatchCallCount);
    }

    [Fact]
    public async Task ConfirmLaunch_ComRevisaoLeveDiferenteMantemRevalidacaoCompleta()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var reviewedSubmission = new AssignmentSubmissionRecord(
            "9001",
            "101",
            "submitted",
            null,
            DateTimeOffset.Parse("2026-09-23T17:00:00Z"),
            DateTimeOffset.Parse("2026-09-23T17:00:00Z"),
            0,
            0,
            false,
            []);
        var currentSubmission = reviewedSubmission with
        {
            ModifiedAt = reviewedSubmission.ModifiedAt!.Value.AddMinutes(1)
        };
        fixture.AssignmentSubmissions.Submissions.Add(currentSubmission);
        item.RecordSubmissionIntegrity(
            new string('a', 64),
            [],
            SubmissionRevisionHash.Compute(reviewedSubmission));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var resolver = new FakeSubmissionContentHashResolver(new string('b', 64));
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            submissionContentHashResolver: resolver,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingWriteEnabled = true,
                McpGradingSecurityWarningsOnly = false
            }),
            submissionsGateway: fixture.AssignmentSubmissions,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Equal(1, resolver.CallCount);
        Assert.Same(currentSubmission, resolver.LastCurrentSubmission);
        Assert.Empty(fixture.Mediator.SavedGrades);
    }

    [Fact]
    public async Task ConfirmLaunch_PublicAuthorizationOnlyDoesNotWriteBeforeDurableWorker()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM",
                ExecuteImmediately: false),
            CancellationToken.None);

        Assert.Equal("authorized", result.Status);
        Assert.Equal(0, result.SentItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Pending, item.CommitStatus);
        var publicationItem = Assert.Single(result.Items!);
        Assert.Equal("authorized", publicationItem.Status);
        Assert.Null(publicationItem.PublishedGrade);
        Assert.Equal(batch.Id, result.BatchJobId);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoHashDaSubmissaoMudouAposRevisao()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var sealedHash = new string('a', 64);
        item.RecordSubmissionIntegrity(sealedHash, []);
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var resolver = new FakeSubmissionContentHashResolver(new string('b', 64));
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository, fixture.GradingRepository, fixture.Confirmations, fixture.Capabilities,
            fixture.ExistingGrades, fixture.SubmissionStatuses, fixture.EnrollmentGateway, fixture.AuditLogs,
            fixture.Mediator, resolver,
             Options.Create(new MoodleUniversalApiFeatureOptions
             {
                 McpGradingWriteEnabled = true,
                 McpGradingSecurityWarningsOnly = true
             }),
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("submission_changed_since_review", auditLog.ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_RetryaLeituraDeStatusDaSubmissaoQuandoAFalhaENetworkTransitória()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        fixture.SubmissionStatuses.Errors.Enqueue(new HttpRequestException("connection reset"));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal(2, fixture.SubmissionStatuses.CallCount);
        Assert.Single(fixture.Mediator.SavedGrades);
    }

    [Fact]
    public async Task ConfirmLaunch_ReconciliaRespostaDeEscritaPerdidaQuandoNotaEFeedbackForamAplicados()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Mediator.OnSaveGrade = command => fixture.ExistingGrades.ExistingGrades.Add(
            new AssignmentExistingGrade(
                command.AssignmentId,
                command.StudentId,
                command.Grade,
                HasGrade: true,
                command.FeedbackText));
        fixture.Mediator.Error = new MoodleApiException(
            MoodleErrorContract.NetworkError,
            "resposta perdida");
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus);
        Assert.Contains(fixture.AuditLogs.Logs, log => log.Status == "commit_reconciled_succeeded");
        Assert.Single(fixture.Mediator.SavedGrades);
    }

    [Fact]
    public async Task ConfirmLaunch_AposRestartReconciliaNotaPersistidaSemReenviarWrite()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();

        // Build the same preflight payload used by the public flow. The
        // simulated process then dies after Moodle applied the values but
        // before the item checkpoint was persisted.
        var preview = await new CreateGradingLaunchPreviewCommandHandler(
            fixture.GradingRepository,
            fixture.PendingActions,
            fixture.CurrentUser,
            fixture.SettingsGateway,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            gradebookGateway: fixture.GradebookGateway).Handle(
                new CreateGradingLaunchPreviewCommand(batch.Id, [], OnlyReviewed: true),
                CancellationToken.None);
        var payload = fixture.PendingActions.LastPayload!;
        var pendingAction = new PendingMoodleAction
        {
            Id = fixture.PendingActions.PendingActionId,
            ToolName = "criar_previa_lancamento_lote",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            CreatedBySubject = "teacher-1",
            CreatedByMoodleUserId = 321,
            CourseId = 10,
            PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            PreviewJson = "{}",
            ConfirmationText = preview.ConfirmationText,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IdempotencyKey = "restart-recovery",
            CorrelationId = "restart-recovery-audit"
        };
        pendingAction.Authorize("teacher-1", DateTimeOffset.UtcNow);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";
        fixture.ExistingGrades.ExistingGrades.Add(new AssignmentExistingGrade(
            "501",
            "101",
            payload.Items[0].Grade,
            HasGrade: true,
            payload.Items[0].FeedbackText));

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                preview.ConfirmationText,
                ExecuteImmediately: true),
            CancellationToken.None);

        Assert.Equal("executed", result.Status);
        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Contains(fixture.AuditLogs.Logs, log => log.Status == "commit_recovered_succeeded");
    }

    [Fact]
    public async Task ConfirmLaunch_AposCrashSemResultadoNaoReenviaEnquantoEscritaAntigaPodeEstarAtiva()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        item.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow.AddMinutes(-2));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        pendingAction.Authorize("teacher-1", DateTimeOffset.UtcNow);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                ConfirmationText: null,
                ExecuteImmediately: true),
            CancellationToken.None);

        Assert.Equal("execution_unknown", result.Status);
        Assert.Equal(0, result.SentItems);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
        Assert.Equal(PendingActionStatus.ExecutionUnknown, pendingAction.Status);
        Assert.NotNull(item.CommitWriteIntentAt);
        Assert.Equal("write_intent_without_result", item.LastErrorCode);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Contains(fixture.AuditLogs.Logs, log =>
            log.Status == "commit_recovery_execution_unknown" &&
            log.ErrorCode == "write_intent_without_result");
    }

    [Fact]
    public async Task ConfirmLaunch_TimeoutSemNotaNaLeituraMantemResultadoDesconhecido()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        pendingAction.Authorize("teacher-1", DateTimeOffset.UtcNow);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";
        fixture.Mediator.Error = new MoodleApiException(
            MoodleErrorContract.NetworkError,
            "resposta perdida");
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("execution_unknown", result.Status);
        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
        Assert.Equal(PendingActionStatus.ExecutionUnknown, pendingAction.Status);
        Assert.Null(pendingAction.NextExecutionAttemptAt);
        Assert.Equal(0, result.RetryScheduledItems);
        Assert.Contains(fixture.AuditLogs.Logs, log => log.Status == "commit_execution_unknown");
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Equal(MoodleErrorContract.NetworkError, item.LastErrorCode);
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Operation == "grading_publication" &&
            measurement.Phase == "queue" &&
            measurement.Result == "started");
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Operation == "grading_publication" &&
            measurement.Phase == "preflight_block" &&
            measurement.Result == "passed");
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Operation == "grading_publication" &&
            measurement.Phase == "write" &&
            measurement.Result == "network_error");
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Operation == "grading_publication" &&
            measurement.Phase == "reconcile" &&
            measurement.Result == "inconclusive");

        // A later Moodle read may finally observe an in-flight write. The
        // original uncertain action must remain blocked from resending until
        // an operator reconciles it with that evidence.
        fixture.ExistingGrades.ExistingGrades.Add(new AssignmentExistingGrade(
            "501",
            "101",
            8.5m,
            HasGrade: true,
            "Feedback final revisado."));
        var repeated = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, ConfirmationText: null),
            CancellationToken.None);

        Assert.Equal("execution_unknown", repeated.Status);
        Assert.Equal(pendingAction.Id, repeated.PendingActionId);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
        Assert.Single(fixture.Mediator.SavedGrades);
    }

    [Fact]
    public async Task ConfirmLaunch_NaoPersisteResultadoQuandoLeaseExpiraDuranteEscritaRemota()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        pendingAction.Authorize("teacher-1", DateTimeOffset.UtcNow);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";
        fixture.Mediator.OnSaveGrade = _ => fixture.PendingRepository.LoseLeaseOnRenewal = true;

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM",
                ExecuteImmediately: true),
            CancellationToken.None);

        Assert.Equal("executing", result.Status);
        Assert.Equal(1, result.SentItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Equal(1, fixture.PendingRepository.LeaseRenewalCallCount);
        // The review and write intent are durable before the POST. The stale
        // executor must not flush the remote result after losing its lease.
        Assert.Equal(3, fixture.GradingRepository.SaveChangesCallCount);
    }

    [Fact]
    public async Task ConfirmLaunch_Publica95FeedbacksSemNotaEmGruposPorAtividadeSemDuplicarAlvos()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501, 502, 503], "teacher-1", 321, totalItems: 95);
        var gradebookTemplate = Assert.IsType<FakeMoodleGradebookGateway>(fixture.GradebookGateway)
            .Gradebook.Items.Single();
        fixture.GradebookGateway = new FakeMoodleGradebookGateway(
            new CourseGradebook("10", "101", new[] { 501L, 502L, 503L }
                .Select(assignmentId => gradebookTemplate with
                {
                    Id = $"grade-item-{assignmentId}",
                    ItemInstance = assignmentId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })
                .ToArray()),
            fixture.ExistingGrades.ExistingGrades);
        var items = new List<AssistedGradingItem>();
        var targetIndex = 0;
        foreach (var (assignmentId, itemCount) in new[] { (501L, 45), (502L, 30), (503L, 20) })
        {
            for (var index = 0; index < itemCount; index++)
            {
                var studentId = 10_000 + targetIndex++;
                var submissionId = 20_000 + studentId;
                var item = AssistedGradingItem.Create(
                    batch.Id,
                    batch.CourseId,
                    assignmentId,
                    submissionId,
                    studentId,
                    attemptNumber: 0);
                item.SetDraft(null, 0.9m, "Synthetic feedback only.");
                item.ApplyTeacherReview(null, "Synthetic feedback only.", "teacher-1", 321, "approved", "ok");
                var context = GradingContext.Build(
                    item.Id,
                    batch.Id,
                    batch.CourseId.ToString(),
                    assignmentId.ToString(),
                    submissionId.ToString(),
                    studentId.ToString(),
                    "Synthetic assignment",
                    "Synthetic criterion",
                    null,
                    10m,
                    null,
                    "Synthetic submission",
                    [],
                    null,
                    null);
                item.RecordContextSnapshot(GradingContextSnapshotFactory.Create(
                    item,
                    context,
                    new GradingContextOptions(
                        IncludeRubric: true,
                        IncludeSubmissionFiles: true,
                        IncludeCourseMaterials: false)));
                items.Add(item);
                fixture.EnrollmentGateway.EnrolledUserIds.Add(studentId.ToString());
                fixture.SubmissionStatuses.Statuses.Add(new AssignmentSubmissionAttemptStatus(
                    assignmentId.ToString(),
                    studentId.ToString(),
                    0,
                    "submitted"));
            }
        }

        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.AddRange(items);
        var groups = GradingPublicationPlanGrouping.CreateGroups(items, maxItems: 20);
        var targetWriteCounts = new Dictionary<(string AssignmentId, string StudentId), int>();

        foreach (var group in groups)
        {
            var groupItems = group.GradingItemIds.Select(id => items.Single(item => item.Id == id)).ToArray();
            var connectionKey = MoodleAssignmentGradeScaleResolver.ConnectionScope(batch);
            fixture.AssignmentSubmissions.Submissions.Clear();
            fixture.AssignmentSubmissions.Submissions.AddRange(groupItems.Select(item => new AssignmentSubmissionRecord(
                "synthetic-submission",
                item.MoodleUserId.ToString(),
                "submitted",
                null,
                null,
                null,
                0,
                0,
                false,
                [])));
            var payloadItems = groupItems.Select(item =>
                CreateCurrentPayloadItem(fixture, batch, item, connectionKey, "Synthetic feedback only.")).ToArray();
            var payload = new GradingLaunchPayload(
                batch.Id,
                payloadItems,
                PublicationId: Guid.NewGuid(),
                ConnectionKey: connectionKey,
                ContractVersion: GradingLaunchPayload.CurrentContractVersion);
            var action = new PendingMoodleAction
            {
                Id = Guid.NewGuid(),
                ToolName = "criar_previa_lancamento_lote",
                RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
                CreatedBySubject = "teacher-1",
                CreatedByMoodleUserId = 321,
                CourseId = batch.CourseId,
                PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                PreviewJson = "{}",
                ConfirmationText = $"CONFIRMAR GRUPO {group.AssignmentId}-{group.GroupIndex}",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                CorrelationId = Guid.NewGuid().ToString("N")
            };
            fixture.PendingRepository.Actions.Add(action);

            var result = await new ConfirmMoodleBatchLaunchCommandHandler(
                fixture.PendingRepository,
                fixture.GradingRepository,
                fixture.Confirmations,
                fixture.Capabilities,
                fixture.ExistingGrades,
                fixture.SubmissionStatuses,
                fixture.EnrollmentGateway,
                fixture.AuditLogs,
                fixture.Mediator,
                submissionsGateway: fixture.AssignmentSubmissions,
                telemetry: fixture.Telemetry,
                assignmentSettingsGateway: fixture.SettingsGateway,
                gradebookGateway: fixture.GradebookGateway).Handle(
                new ConfirmMoodleBatchLaunchCommand(action.Id, action.ConfirmationText),
                CancellationToken.None);

            Assert.Equal("confirmed", result.Status);
            Assert.Equal(groupItems.Length, result.SentItems);
            Assert.Equal(0, result.FailedItems);
        }

        foreach (var write in fixture.Mediator.SavedGrades)
        {
            var target = (write.AssignmentId, write.StudentId);
            targetWriteCounts[target] = targetWriteCounts.GetValueOrDefault(target) + 1;
            Assert.Null(write.Grade);
        }

        Assert.Equal(6, groups.Count);
        Assert.Equal(95, fixture.Mediator.SavedGrades.Count);
        Assert.Equal(95, targetWriteCounts.Count);
        Assert.All(targetWriteCounts.Values, count => Assert.Equal(1, count));
        Assert.All(items, item => Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus));
    }

    [Fact]
    public async Task ConfirmLaunch_FalhaAmbiguaInterrompeGrupoEDeixaProximoItemProntoParaRecuperacao()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var items = new[]
        {
            CreateReviewedSyntheticItem(batch, assignmentId: 501, submissionId: 9001, moodleUserId: 101),
            CreateReviewedSyntheticItem(batch, assignmentId: 501, submissionId: 9002, moodleUserId: 102)
        };
        fixture.EnrollmentGateway.EnrolledUserIds.Add("102");
        fixture.SubmissionStatuses.Statuses.AddRange(items.Select(item => new AssignmentSubmissionAttemptStatus(
            "501",
            item.MoodleUserId.ToString(),
            0,
            "submitted")));
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.AddRange(items);

        var connectionKey = MoodleAssignmentGradeScaleResolver.ConnectionScope(batch);
        var payload = new GradingLaunchPayload(
            batch.Id,
            items.Select(item => CreateCurrentPayloadItem(
                fixture,
                batch,
                item,
                connectionKey,
                "Synthetic feedback only.")).ToArray(),
            PublicationId: Guid.NewGuid(),
            ConnectionKey: connectionKey,
            ContractVersion: GradingLaunchPayload.CurrentContractVersion);
        var action = new PendingMoodleAction
        {
            Id = Guid.NewGuid(),
            ToolName = "criar_previa_lancamento_lote",
            RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
            CreatedBySubject = "teacher-1",
            CreatedByMoodleUserId = 321,
            CourseId = 10,
            PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            PreviewJson = "{}",
            ConfirmationText = "CONFIRMAR GRUPO SINTETICO",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        action.Authorize("teacher-1", DateTimeOffset.UtcNow);
        fixture.PendingRepository.Actions.Add(action);
        fixture.Confirmations.Status = "authorized";
        fixture.Mediator.OnSaveGrade = command =>
        {
            if (command.StudentId == "101")
            {
                fixture.Mediator.Error = new MoodleApiException(MoodleErrorContract.NetworkError, "synthetic lost response");
            }
        };

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(action.Id, action.ConfirmationText, ExecuteImmediately: true),
            CancellationToken.None);

        Assert.Equal("execution_unknown", result.Status);
        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, items[0].CommitStatus);
        Assert.Equal(GradingCommitStatus.Pending, items[1].CommitStatus);
        Assert.NotNull(items[1].FinalFeedback);
        Assert.Contains(result.Items!, item =>
            item.GradingItemId == items[1].Id && item.CommitStatus == GradingCommitStatus.Pending.ToString());
    }

    private static AssistedGradingItem CreateReviewedSyntheticItem(
        AssistedGradingBatch batch,
        long assignmentId,
        long submissionId,
        long moodleUserId)
    {
        var item = AssistedGradingItem.Create(batch.Id, batch.CourseId, assignmentId, submissionId, moodleUserId, 0);
        item.SetDraft(null, 0.9m, "Synthetic feedback only.");
        item.ApplyTeacherReview(null, "Synthetic feedback only.", "teacher-1", 321, "approved", "ok");
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            batch.CourseId.ToString(),
            assignmentId.ToString(),
            submissionId.ToString(),
            moodleUserId.ToString(),
            "Synthetic assignment",
            "Synthetic criterion",
            null,
            10m,
            null,
            "Synthetic submission",
            [],
            null,
            null);
        item.RecordContextSnapshot(GradingContextSnapshotFactory.Create(
            item,
            context,
            new GradingContextOptions(
                IncludeRubric: true,
                IncludeSubmissionFiles: true,
                IncludeCourseMaterials: false)));
        return item;
    }

    [Fact]
    public async Task GetPublicationStatus_ReadyItemsAreIncludedInCounts()
    {
        var fixture = new Fixture();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 6, gradingRunId: run.Id);
        fixture.GradingRepository.Runs.Add(run);
        fixture.GradingRepository.Batches.Add(batch);

        var items = new List<AssistedGradingItem>();
        var payloadItems = new List<GradingLaunchPayloadItem>();
        var previewItems = new List<GradingLaunchPreviewItem>();
        var claimStatuses = new Dictionary<Guid, string>();
        for (var index = 0; index < 6; index++)
        {
            var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101 + index, 0);
            item.SetDraft(null, 0.9m, $"Rascunho {index}.");
            item.ApplyTeacherReview(null, $"Feedback revisado {index}.", "teacher-1", 321, "approved", null);
            if (index < 2)
            {
                item.MarkCommitSucceeded();
                claimStatuses[item.Id] = "Succeeded";
            }
            else if (index == 2)
            {
                Assert.True(item.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow));
                item.MarkCommitExecutionUnknown("Resposta perdida.", "request_timeout");
                claimStatuses[item.Id] = "ExecutionUnknown";
            }
            else
            {
                claimStatuses[item.Id] = "Authorized";
            }

            items.Add(item);
            payloadItems.Add(new GradingLaunchPayloadItem(
                item.Id,
                "10",
                "501",
                item.MoodleUserId.ToString(),
                null,
                item.FinalFeedback!,
                0,
                "draft-hash"));
            previewItems.Add(new GradingLaunchPreviewItem(
                item.Id,
                "501",
                item.MoodleUserId.ToString(),
                null,
                item.FinalFeedback!,
                ReviewStatus: "Reviewed"));
        }

        fixture.GradingRepository.Items.AddRange(items);
        var publicationId = Guid.NewGuid();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            PayloadJson = JsonSerializer.Serialize(
                new GradingLaunchPayload(batch.Id, payloadItems, PublicationId: publicationId),
                options),
            PreviewJson = JsonSerializer.Serialize(new
            {
                batchJobId = batch.Id,
                totalItems = 6,
                readyItems = 6,
                blockedItems = 0,
                launches = previewItems,
                warnings = Array.Empty<string>()
            }, options),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        action.Authorize("teacher-1", DateTimeOffset.UtcNow);
        action.MarkExecutionUnknown("Resultado remoto desconhecido.");
        fixture.PendingRepository.Actions.Add(action);
        fixture.GradingRepository.PublicationClaimStatuses[publicationId] = claimStatuses;

        var handler = new GetGradingPublicationStatusQueryHandler(
            fixture.GradingRepository,
            fixture.PendingRepository,
            fixture.CurrentUser);
        var result = await handler.Handle(
            new GetGradingPublicationStatusQuery(PendingActionId: action.Id, Page: 1, PageSize: 2),
            CancellationToken.None);

        var serialized = JsonSerializer.SerializeToDocument(result, options);
        var counts = serialized.RootElement.GetProperty("publicationCounts");
        Assert.Equal(6, counts.GetProperty("selectedItems").GetInt32());
        Assert.Equal(2, counts.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, counts.GetProperty("unknown").GetInt32());
        Assert.Equal(3, counts.GetProperty("ready").GetInt32());
        Assert.Equal(6, counts.EnumerateObject()
            .Where(property => property.Name != "selectedItems")
            .Sum(property => property.Value.GetInt32()));
        Assert.Equal("attention", result.NextAction);
        Assert.Equal(2, result.Items.Count);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task GetPublicationStatus_ByRunListsRelatedActionsAndByActionPreservesEachResult()
    {
        var fixture = new Fixture();
        var run = GradingRun.Create("teacher-1");
        var completedBatch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, gradingRunId: run.Id);
        var interruptedBatch = AssistedGradingBatch.Create(10, [502], "teacher-1", 321, 2, gradingRunId: run.Id);
        var completedItem = CreateReviewedPublicationItem(completedBatch.Id, 501, 9001, 101, "Feedback publicado.");
        completedItem.MarkCommitSucceeded();
        var unknownItem = CreateReviewedPublicationItem(interruptedBatch.Id, 502, 9002, 102, "Feedback pendente.");
        Assert.True(unknownItem.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow));
        unknownItem.MarkCommitExecutionUnknown("Resposta perdida.", MoodleErrorContract.NetworkError);
        var neverAttemptedItem = CreateReviewedPublicationItem(interruptedBatch.Id, 502, 9003, 103, "Ainda não enviado.");

        fixture.GradingRepository.Runs.Add(run);
        fixture.GradingRepository.Batches.AddRange(completedBatch, interruptedBatch);
        fixture.GradingRepository.Items.AddRange(completedItem, unknownItem, neverAttemptedItem);
        var completedPublicationId = Guid.NewGuid();
        var completedAction = CreateStatusAction(
            completedBatch.Id,
            [completedItem],
            PendingActionStatus.Executed,
            completedPublicationId);
        var interruptedPublicationId = Guid.NewGuid();
        var interruptedAction = CreateStatusAction(
            interruptedBatch.Id,
            [unknownItem, neverAttemptedItem],
            PendingActionStatus.ExecutionUnknown,
            interruptedPublicationId);
        fixture.PendingRepository.Actions.AddRange(completedAction, interruptedAction);
        fixture.GradingRepository.PublicationClaimStatuses[completedPublicationId] =
            new Dictionary<Guid, string> { [completedItem.Id] = "Succeeded" };
        fixture.GradingRepository.PublicationClaimStatuses[interruptedPublicationId] = new Dictionary<Guid, string>
        {
            [unknownItem.Id] = "ExecutionUnknown",
            [neverAttemptedItem.Id] = "Authorized"
        };

        var handler = new GetGradingPublicationStatusQueryHandler(
            fixture.GradingRepository,
            fixture.PendingRepository,
            fixture.CurrentUser);
        var byRun = await handler.Handle(
            new GetGradingPublicationStatusQuery(GradingRunId: run.Id),
            CancellationToken.None);
        var completed = await handler.Handle(
            new GetGradingPublicationStatusQuery(PendingActionId: completedAction.Id),
            CancellationToken.None);
        var interrupted = await handler.Handle(
            new GetGradingPublicationStatusQuery(PendingActionId: interruptedAction.Id),
            CancellationToken.None);

        Assert.Equal(interruptedAction.Id, byRun.PendingActionId);
        Assert.Contains(completedAction.Id, byRun.RelatedPendingActionIds);
        Assert.Contains(interruptedAction.Id, byRun.RelatedPendingActionIds);
        Assert.Equal("succeeded", completed.PublicationState);
        Assert.Equal("unknown", interrupted.PublicationState);
        Assert.Equal(2, interrupted.PublicationCounts.SelectedItems);
        Assert.Equal(1, interrupted.PublicationCounts.Unknown);
        Assert.Equal(1, interrupted.PublicationCounts.Ready);

        static AssistedGradingItem CreateReviewedPublicationItem(
            Guid batchId,
            long assignmentId,
            long submissionId,
            long studentId,
            string feedback)
        {
            var item = AssistedGradingItem.Create(batchId, 10, assignmentId, submissionId, studentId, 0);
            item.SetDraft(null, 0.9m, feedback);
            item.ApplyTeacherReview(null, feedback, "teacher-1", 321, "approved", null);
            return item;
        }

        static PendingMoodleAction CreateStatusAction(
            Guid batchId,
            IReadOnlyList<AssistedGradingItem> items,
            PendingActionStatus outcome,
            Guid publicationId)
        {
            var payloadItems = items.Select(item => new GradingLaunchPayloadItem(
                item.Id,
                "10",
                item.AssignmentId.ToString(),
                item.MoodleUserId.ToString(),
                item.FinalGrade,
                item.FinalFeedback!,
                item.AttemptNumber,
                GradingDraftVersionHash.Compute(item))).ToArray();
            var previewItems = items.Select(item => new GradingLaunchPreviewItem(
                item.Id,
                item.AssignmentId.ToString(),
                item.MoodleUserId.ToString(),
                item.FinalGrade,
                item.FinalFeedback!,
                ReviewStatus: "Reviewed")).ToArray();
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var action = new PendingMoodleAction
            {
                ToolName = "criar_previa_lancamento_lote",
                CreatedBySubject = "teacher-1",
                PayloadJson = JsonSerializer.Serialize(
                    new GradingLaunchPayload(batchId, payloadItems, PublicationId: publicationId),
                    options),
                PreviewJson = JsonSerializer.Serialize(new
                {
                    batchJobId = batchId,
                    totalItems = items.Count,
                    readyItems = items.Count,
                    blockedItems = 0,
                    launches = previewItems,
                    warnings = Array.Empty<string>()
                }, options),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            action.Authorize("teacher-1", DateTimeOffset.UtcNow);
            if (outcome == PendingActionStatus.Executed)
            {
                action.MarkExecuted();
            }
            else
            {
                action.MarkExecutionUnknown("Resultado remoto desconhecido.");
            }

            return action;
        }
    }

    [Fact]
    public async Task ConfirmLaunch_Http503ComErroEstruturadoNaoLiberaReenvio()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        pendingAction.Authorize("teacher-1", DateTimeOffset.UtcNow);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";
        fixture.Mediator.Error = new MoodleApiException(
            MoodleErrorContract.ApiError,
            "erro apos envio",
            httpStatusCode: 503);

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, ConfirmationText: null),
            CancellationToken.None);

        Assert.Equal("execution_unknown", result.Status);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
        Assert.Equal(PendingActionStatus.ExecutionUnknown, pendingAction.Status);
        Assert.NotNull(item.CommitWriteIntentAt);
        Assert.Equal(0, result.RetryScheduledItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Phase == "write" && measurement.Result == "http_5xx");
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Phase == "reconcile" && measurement.Result == "inconclusive");
    }

    [Fact]
    public async Task ConfirmLaunch_RejeicaoHttp429SemNotaFalhaSemAgendarNovoEnvio()
    {
        var fixture = new Fixture();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8.5m, 0.8m, "Feedback final revisado.");
        AttachVersionedContext(item, batch);
        fixture.GradingRepository.Batches.Add(batch);
        fixture.GradingRepository.Items.Add(item);
        var previewHash = GradingDraftVersionHash.Compute(item);
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        pendingAction.Authorize("teacher-1", DateTimeOffset.UtcNow);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";
        fixture.Mediator.Error = new MoodleApiException(
            MoodleErrorContract.ApiError,
            "rejeitado pelo servidor",
            httpStatusCode: 429);

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, ConfirmationText: null),
            CancellationToken.None);

        Assert.Equal("partial_failure", result.Status);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Equal(GradingReviewStatus.Reviewed, item.ReviewStatus);
        Assert.Equal(previewHash, GradingDraftVersionHash.Compute(item));
        Assert.Equal(PendingActionStatus.PartiallyCompleted, pendingAction.Status);
        Assert.Null(pendingAction.NextExecutionAttemptAt);
        Assert.Equal(0, item.CommitRetryCount);
        Assert.Equal("moodle_rate_limited_not_applied", item.LastErrorCode);
        Assert.Equal(0, result.RetryScheduledItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Phase == "write" && measurement.Result == "http_429");
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Phase == "reconcile" && measurement.Result == "not_applied");
    }

    [Fact]
    public async Task ConfirmLaunch_RetomaRetryAgendadoComAcaoOriginalSemNovaFraseDeConfirmacao()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        pendingAction.Authorize("teacher-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        var retryAt = DateTimeOffset.UtcNow.AddMilliseconds(150);
        item.ScheduleCommitRetry(
            "A leitura confirmou que a nota nao foi aplicada.",
            "moodle_write_reconciled_not_applied",
            retryAt);
        pendingAction.MarkPartiallyCompleted(
            "Retry seguro agendado.",
            retryAt);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";
        fixture.Mediator.OnSaveGrade = _ =>
        {
            Assert.NotNull(item.CommitWriteIntentAt);
            Assert.Contains(fixture.AuditLogs.Logs, log => log.Status == "commit_write_intent");
        };
        await Task.Delay(200);

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                ConfirmationText: null,
                ExecuteImmediately: true),
            CancellationToken.None);

        Assert.True(
            result.Status == "executed",
            $"status={result.Status}; failures={string.Join(" | ", result.Failures.Select(failure => failure.Message))}; item={item.CommitStatus}/{item.LastErrorCode}; action={pendingAction.Status}/{pendingAction.LastExecutionError}");
        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal(1, item.CommitRetryCount);
        Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus);
        Assert.Equal(PendingActionStatus.Executed, pendingAction.Status);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Contains(fixture.AuditLogs.Logs, log => log.Status == "commit_write_intent");
    }

    [Fact]
    public async Task ConfirmLaunch_AceitaHashDePreviaLegadaQuandoRascunhoContinuaInalterado()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var legacyDraftHash = GradingDraftVersionHash.ComputeLegacy(item);
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id, draftVersionHash: legacyDraftHash);
        pendingAction.Authorize("teacher-1", DateTimeOffset.UtcNow.AddMinutes(-1));
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Confirmations.Status = "authorized";

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, ConfirmationText: null),
            CancellationToken.None);

        Assert.Equal("executed", result.Status);
        Assert.Equal(1, result.SentItems);
        Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus);
        Assert.Single(fixture.Mediator.SavedGrades);
    }

    [Fact]
    public async Task ConfirmLaunch_MantemExecucaoDesconhecidaQuandoLeituraPosteriorDivergeDoLancamento()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Mediator.OnSaveGrade = command => fixture.ExistingGrades.ExistingGrades.Add(
            new AssignmentExistingGrade(
                command.AssignmentId,
                command.StudentId,
                command.Grade - 1,
                HasGrade: true,
                "Feedback diferente."));
        fixture.Mediator.Error = new MoodleApiException(
            MoodleErrorContract.NetworkError,
            "resposta perdida");
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("execution_unknown", result.Status);
        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
        Assert.Equal(PendingActionStatus.ExecutionUnknown, pendingAction.Status);
        Assert.Contains(fixture.AuditLogs.Logs, log => log.Status == "commit_execution_unknown");
        Assert.Single(fixture.Mediator.SavedGrades);
    }

    [Fact]
    public async Task ConfirmLaunch_NotaCoincidenteComFeedbackDiferenteNaoConfirmaPublicacao()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Mediator.OnSaveGrade = command => fixture.ExistingGrades.ExistingGrades.Add(
            new AssignmentExistingGrade(
                command.AssignmentId,
                command.StudentId,
                command.Grade,
                HasGrade: true,
                "Feedback ainda nao atualizado."));
        fixture.Mediator.Error = new MoodleApiException(
            MoodleErrorContract.NetworkError,
            "resposta perdida");

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("execution_unknown", result.Status);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
        Assert.Equal(PendingActionStatus.ExecutionUnknown, pendingAction.Status);
        Assert.Equal(0, result.SentItems);
        Assert.Single(fixture.Mediator.SavedGrades);
    }

    [Fact]
    public async Task ConfirmLaunch_FalhaNaLeituraDeReconciliacaoMantemResultadoDesconhecido()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        fixture.Mediator.OnSaveGrade = _ =>
            fixture.ExistingGrades.Error = new InvalidOperationException("consulta de reconciliacao indisponivel");
        fixture.Mediator.Error = new MoodleApiException(
            MoodleErrorContract.NetworkError,
            "resposta perdida");

        var result = await CreateConfirmHandler(fixture).Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("execution_unknown", result.Status);
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
        Assert.Equal(PendingActionStatus.ExecutionUnknown, pendingAction.Status);
        Assert.Equal(0, result.RetryScheduledItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Equal(MoodleErrorContract.NetworkError, item.LastErrorCode);
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Phase == "write" && measurement.Result == "network_error");
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Phase == "reconcile" && measurement.Result == "inconclusive");
        Assert.DoesNotContain(fixture.Telemetry.Measurements, measurement =>
            measurement.Result.Contains("indisponivel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfirmLaunch_Repetido_NaoReenviaItemJaEnviado()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        item.MarkCommitSucceeded();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaPreviaLegadaSemContextoVersionado()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem(includeContext: false);
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal("grading_context_missing", Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked").ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaPreviaComContextoDivergente()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id, contextHash: "hash-contexto-antigo");
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal("grading_context_hash_mismatch", Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked").ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_ComPreviaObsoleta_NaoEnviaNota()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(
            batch.Id,
            item.Id,
            draftVersionHash: "hash-antigo");
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("rascunho foi alterado", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("grading_draft_version_mismatch", auditLog.ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoJaExisteNotaNoMoodle()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        fixture.ExistingGrades.ExistingGrades.Add(new AssignmentExistingGrade(
            AssignmentId: "501",
            StudentId: "101",
            Grade: 7.5m,
            HasGrade: true));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("nota existente", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("moodle_existing_grade", auditLog.ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_SobrescreveNotaExistenteQuandoAutorizadoNaPrevia()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        fixture.ExistingGrades.ExistingGrades.Add(new AssignmentExistingGrade("501", "101", 7.5m, true));
        fixture.SubmissionStatuses.Statuses.Add(new AssignmentSubmissionAttemptStatus("501", "101", 0, "submitted", HasFeedback: true));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id, allowOverwriteExisting: true);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository, fixture.GradingRepository, fixture.Confirmations,
            fixture.Capabilities, fixture.ExistingGrades, fixture.SubmissionStatuses,
            fixture.EnrollmentGateway, fixture.AuditLogs, fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoNaoConsegueValidarNotaExistente()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        fixture.ExistingGrades.Error = new InvalidOperationException("mod_assign_get_grades indisponivel");
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("Nota e feedback atuais", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("grade_baseline_unavailable", auditLog.ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoTentativaDoMoodleMudou()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        fixture.SubmissionStatuses.Statuses.Add(new AssignmentSubmissionAttemptStatus(
            AssignmentId: "501",
            StudentId: "101",
            AttemptNumber: 1,
            SubmissionStatus: "submitted"));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("tentativa", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("moodle_submission_attempt_mismatch", auditLog.ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoSubmissaoAtualNaoEstaEntregue()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        fixture.SubmissionStatuses.Statuses.Add(new AssignmentSubmissionAttemptStatus(
            AssignmentId: "501",
            StudentId: "101",
            AttemptNumber: 0,
            SubmissionStatus: "new"));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("submissao atual nao esta entregue", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("moodle_submission_not_submitted", auditLog.ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoFuncaoMoodleDeEscritaNaoEstaDisponivel()
    {
        var fixture = new Fixture();
        fixture.Capabilities.Functions.Clear();
        fixture.Capabilities.Functions.Add("mod_assign_get_submissions");
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("mod_assign_save_grade", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("moodle_function_unavailable", auditLog.ErrorCode);
        Assert.Equal("mod_assign_save_grade", auditLog.MoodleFunction);
    }

    [Fact]
    public async Task ConfirmLaunch_ComAvisoTecnicoDeCapabilityContinuaQuandoAEscritaERealizada()
    {
        var fixture = new Fixture();
        fixture.Capabilities.Functions.Clear();
        fixture.Capabilities.Functions.Add("mod_assign_get_submissions");
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingSecurityWarningsOnly = true
            }),
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(pendingAction.Id, "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal(1, result.SentItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Single(fixture.Mediator.SavedGrades);
        Assert.Contains(result.Warnings!, warning => warning.Contains("moodle_function_unavailable", StringComparison.Ordinal));
        Assert.Equal(GradingCommitStatus.Succeeded, item.CommitStatus);
        Assert.Contains(fixture.AuditLogs.Logs, log => log.Status == "commit_succeeded");
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoEstudanteNaoEstaInscritoNoCurso()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        fixture.EnrollmentGateway.EnrolledUserIds.Clear(); // nenhum estudante inscrito
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("nao esta inscrito", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("moodle_student_not_enrolled", auditLog.ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoValidacaoDeEnrollmentFalha()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        fixture.EnrollmentGateway.Error = new InvalidOperationException("Token expirado");
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("validar se o estudante", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("moodle_enrollment_validation_failed", auditLog.ErrorCode);
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoCapabilitiesGatewayLancaExcecao()
    {
        var fixture = new Fixture();
        fixture.Capabilities.Error = new InvalidOperationException("Token expirado ou sem escopo");
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = CreateConfirmHandler(fixture);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("validar a funcao Moodle", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("moodle_function_validation_failed", auditLog.ErrorCode);
        Assert.Contains(fixture.Telemetry.Measurements, measurement =>
            measurement.Phase == "preflight" && measurement.Result == "blocked");
        Assert.DoesNotContain(fixture.Telemetry.Measurements, measurement => measurement.Phase == "write");
    }

    [Fact]
    public async Task ConfirmLaunch_BloqueiaQuandoJaExisteFeedbackNoMoodle()
    {
        var fixture = new Fixture();
        var batch = fixture.CreateBatchWithReviewedItem();
        var item = fixture.GradingRepository.Items.Single();
        // Substituir o status padrão por um com HasFeedback = true
        fixture.SubmissionStatuses.Statuses.Clear();
        fixture.SubmissionStatuses.Statuses.Add(new AssignmentSubmissionAttemptStatus(
            AssignmentId: "501",
            StudentId: "101",
            AttemptNumber: 0,
            SubmissionStatus: "submitted",
            HasFeedback: true));
        var pendingAction = fixture.CreatePendingLaunchAction(batch.Id, item.Id);
        fixture.PendingRepository.Actions.Add(pendingAction);
        var sut = new ConfirmMoodleBatchLaunchCommandHandler(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway);

        var result = await sut.Handle(
            new ConfirmMoodleBatchLaunchCommand(
                pendingAction.Id,
                "CONFIRMAR LANCAMENTO 1 ITEM"),
            CancellationToken.None);

        Assert.Equal(0, result.SentItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Empty(fixture.Mediator.SavedGrades);
        Assert.Equal(GradingCommitStatus.Failed, item.CommitStatus);
        Assert.Contains("feedback existente", result.Failures[0].Message, StringComparison.OrdinalIgnoreCase);
        var auditLog = Assert.Single(fixture.AuditLogs.Logs, log => log.Status == "commit_blocked");
        Assert.Equal("moodle_existing_feedback", auditLog.ErrorCode);
    }

    private static ConfirmMoodleBatchLaunchCommandHandler CreateConfirmHandler(Fixture fixture) =>
        new(
            fixture.PendingRepository,
            fixture.GradingRepository,
            fixture.Confirmations,
            fixture.Capabilities,
            fixture.ExistingGrades,
            fixture.SubmissionStatuses,
            fixture.EnrollmentGateway,
            fixture.AuditLogs,
            fixture.Mediator,
            telemetry: fixture.Telemetry,
            assignmentSettingsGateway: fixture.SettingsGateway,
            gradebookGateway: fixture.GradebookGateway,
            currentUser: fixture.CurrentUser,
            executionContext: fixture.ExecutionContext,
            connectionSelection: fixture.ConnectionSelection);

    private static FakeMoodleGradebookGateway CreateGradebookGateway(
        decimal maximumGrade,
        List<AssignmentExistingGrade>? observedGrades = null) =>
        new(new CourseGradebook("10", "101",
        [
            new GradebookItem(
                "grade-item-77",
                "Atividade",
                "mod",
                "assign",
                null,
                null,
                null,
                0m,
                maximumGrade,
                null,
                null,
                null,
                null,
                null,
                null,
                "501",
                "990",
                ScaleId: 0,
                GradeType: 1,
                FeedbackFieldPresent: true)
        ]), observedGrades);

    private static GradingLaunchPayloadItem CreateCurrentPayloadItem(
        Fixture fixture,
        AssistedGradingBatch batch,
        AssistedGradingItem item,
        string connectionKey,
        string feedbackText)
    {
        var scale = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
            fixture.SettingsGateway.Result,
            batch.CourseId,
            item.AssignmentId,
            connectionKey);
        var assignmentGrade = fixture.ExistingGrades.ExistingGrades.SingleOrDefault(grade =>
            grade.AssignmentId == item.AssignmentId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            grade.StudentId == item.MoodleUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var gradebook = (fixture.GradebookGateway as FakeMoodleGradebookGateway)?.Gradebook;
        var baseline = GradingPublicationGradebookProjection.Combine(
            item.AssignmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            assignmentGrade,
            gradebook) ?? new AssignmentExistingGrade(
                item.AssignmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.MoodleUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                null,
                HasGrade: false,
                FeedbackObserved: true);
        var payloadItem = new GradingLaunchPayloadItem(
            item.Id,
            batch.CourseId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.AssignmentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.MoodleUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.FinalGrade,
            feedbackText,
            item.AttemptNumber,
            GradingDraftVersionHash.Compute(item),
            ContextHash: item.ContextHash,
            MaxGrade: scale.MaximumGrade,
            MinGrade: scale.MinimumGrade,
            GradingMode: scale.GradingMode,
            ScaleSource: scale.Source,
            ScaleVersion: scale.Version,
            GradeOperation: item.FinalGrade is null ? "preserve_grade" : "set_grade",
            BaselineHasGrade: baseline.HasGrade,
            BaselineGrade: baseline.Grade,
            WorkflowMode: "disabled",
            ReleaseState: "not_applicable",
            VisibilityState: "unknown");
        return payloadItem with
        {
            PreflightHash = GradingPublicationGradebookProjection.ComputePreflightHash(
                connectionKey,
                payloadItem,
                baseline)
        };
    }

    private sealed class Fixture
    {
        public Fixture(string currentUserSubject = "teacher-1", IReadOnlyCollection<string>? scopes = null)
        {
            CurrentUser = new FakeCurrentUserContext(currentUserSubject, scopes);
            GradebookGateway = CreateGradebookGateway(10m, ExistingGrades.ExistingGrades);
            Mediator.OnSuccessfulSaveGrade = command =>
            {
                var existingIndex = ExistingGrades.ExistingGrades.FindIndex(grade =>
                    grade.AssignmentId == command.AssignmentId && grade.StudentId == command.StudentId);
                var observed = new AssignmentExistingGrade(
                    command.AssignmentId,
                    command.StudentId,
                    command.Grade,
                    HasGrade: command.Grade is not null,
                    Feedback: command.FeedbackText,
                    FeedbackObserved: true);
                if (existingIndex >= 0)
                {
                    ExistingGrades.ExistingGrades[existingIndex] = observed;
                }
                else
                {
                    ExistingGrades.ExistingGrades.Add(observed);
                }
            };
        }

        public FakeGradingReviewRepository GradingRepository { get; } = new();
        public FakePendingActionService PendingActions { get; } = new();
        public FakePendingActionRepository PendingRepository { get; } = new();
        public FakeActionConfirmationService Confirmations { get; } = new();
        public FakeMoodleGradingCapabilitiesGateway Capabilities { get; } = new();
        public FakeMoodleAssignmentGradeReadGateway ExistingGrades { get; } = new();
        public FakeMoodleAssignmentSubmissionStatusGateway SubmissionStatuses { get; } = new();
        public FakeMoodleAssignmentSubmissionsGateway AssignmentSubmissions { get; } = new();
        public FakeMoodleParticipantsGateway EnrollmentGateway { get; } = new();
        public FakeAuditLogRepository AuditLogs { get; } = new();
        public FakeMediator Mediator { get; } = new();
        public RecordingGradingOperationTelemetry Telemetry { get; } = new();
        public FakeCurrentUserContext CurrentUser { get; }
        public FakeMoodleAssignmentSettingsGateway SettingsGateway { get; } = new();
        public FakeMoodleConnectionSelection ConnectionSelection { get; } = new();
        public FakeConnectorExecutionContext ExecutionContext { get; } = new();
        public IMoodleGradebookGateway? GradebookGateway { get; set; }

        public AssistedGradingBatch CreateBatchWithReviewedItem(bool includeContext = true)
        {
            var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
            var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
            item.SetDraft(8m, 0.8m, "Rascunho.");
            item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321, "approved", "ok");
            if (includeContext)
            {
                AttachVersionedContext(item, batch);
            }
            GradingRepository.Batches.Add(batch);
            GradingRepository.Items.Add(item);
            return batch;
        }

        public PendingMoodleAction CreatePendingLaunchAction(
            Guid batchId,
            Guid gradingItemId,
            string? draftVersionHash = null,
            bool allowOverwriteExisting = false,
            string? contextHash = null,
            string? connectionKey = null)
        {
            if (!SubmissionStatuses.Statuses.Any(status =>
                status.AssignmentId == "501" &&
                status.StudentId == "101"))
            {
                SubmissionStatuses.Statuses.Add(new AssignmentSubmissionAttemptStatus(
                    AssignmentId: "501",
                    StudentId: "101",
                    AttemptNumber: 0,
                SubmissionStatus: "submitted"));
            }

            var batch = GradingRepository.Batches.Single(candidate => candidate.Id == batchId);
            var effectiveConnectionKey = connectionKey ?? MoodleAssignmentGradeScaleResolver.ConnectionScope(batch);
            var scale = MoodleAssignmentGradeScaleResolver.FromAssignmentSettings(
                SettingsGateway.Result,
                batch.CourseId,
                501,
                effectiveConnectionKey);
            var reviewedItem = GradingRepository.Items.Single(item => item.Id == gradingItemId);
            var observedAssignmentGrade = ExistingGrades.ExistingGrades.SingleOrDefault(grade =>
                grade.AssignmentId == "501" && grade.StudentId == "101");
            var observedGradebook = (GradebookGateway as FakeMoodleGradebookGateway)?.Gradebook;
            var baseline = GradingPublicationGradebookProjection.Combine(
                "501",
                "101",
                observedAssignmentGrade,
                observedGradebook) ?? new AssignmentExistingGrade(
                    "501",
                    "101",
                    null,
                    HasGrade: false,
                    FeedbackObserved: true);
            var payloadItem = new GradingLaunchPayloadItem(
                gradingItemId,
                "10",
                "501",
                "101",
                8.5m,
                "Feedback final revisado.",
                AttemptNumber: 0,
                DraftVersionHash: draftVersionHash ?? GradingDraftVersionHash.Compute(reviewedItem),
                ContextHash: contextHash ?? reviewedItem.ContextHash,
                MaxGrade: scale.MaximumGrade,
                MinGrade: scale.MinimumGrade,
                GradingMode: scale.GradingMode,
                ScaleSource: scale.Source,
                ScaleVersion: scale.Version,
                GradeOperation: "set_grade",
                BaselineHasGrade: baseline.HasGrade,
                BaselineGrade: baseline.Grade,
                WorkflowMode: "disabled",
                ReleaseState: "not_applicable",
                VisibilityState: "unknown");
            payloadItem = payloadItem with
            {
                PreflightHash = GradingPublicationGradebookProjection.ComputePreflightHash(
                    effectiveConnectionKey,
                    payloadItem,
                    baseline)
            };

            var payload = new GradingLaunchPayload(
                batchId,
                [payloadItem],
                allowOverwriteExisting,
                PublicationId: Guid.NewGuid(),
                ConnectionKey: effectiveConnectionKey,
                ContractVersion: GradingLaunchPayload.CurrentContractVersion);

            return new PendingMoodleAction
            {
                Id = PendingActions.PendingActionId,
                ToolName = "criar_previa_lancamento_lote",
                RiskLevel = ToolRiskLevel.CriticalHumanConfirmedWrite,
                CreatedBySubject = "teacher-1",
                CourseId = 10,
                PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                PreviewJson = "{}",
                ConfirmationText = "CONFIRMAR LANCAMENTO 1 ITEM",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                IdempotencyKey = "idem-1",
                CorrelationId = "audit-1"
            };
        }
    }

    private static GradingContextSnapshot AttachVersionedContext(
        AssistedGradingItem item,
        AssistedGradingBatch batch,
        bool attachToItem = true)
    {
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            "9001",
            "101",
            "Enunciado de teste",
            "Criterio de teste",
            null,
            10m,
            null,
            "Entrega de teste",
            [],
            null,
            null);
        var snapshot = GradingContextSnapshotFactory.Create(
            item,
            context,
            new GradingContextOptions(
                IncludeRubric: true,
                IncludeSubmissionFiles: true,
                IncludeCourseMaterials: false));
        if (attachToItem)
        {
            item.RecordContextSnapshot(snapshot);
        }

        return snapshot;
    }

    private sealed class RecordingGradingOperationTelemetry : IGradingOperationTelemetry
    {
        public List<GradingPhaseMeasurement> Measurements { get; } = [];

        public void RecordPhase(
            string operation,
            string phase,
            string result,
            double durationMs,
            int queryCount = 0,
            int itemCount = 0,
            long bytes = 0) =>
            Measurements.Add(new GradingPhaseMeasurement(operation, phase, result, durationMs, itemCount));
    }

    private sealed record GradingPhaseMeasurement(
        string Operation,
        string Phase,
        string Result,
        double DurationMs,
        int ItemCount);

    private static GradingContextSnapshot AttachBlockedVersionedContext(
        AssistedGradingItem item,
        AssistedGradingBatch batch)
    {
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            "9001",
            "101",
            assignmentStatement: null,
            criteria: null,
            rubricDescription: null,
            maxGrade: 10m,
            gradeScale: null,
            submissionText: "Entrega de teste",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: null);
        var snapshot = GradingContextSnapshotFactory.Create(
            item,
            context,
            new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        return snapshot;
    }

    private sealed class FakeCurrentUserContext(string subject, IReadOnlyCollection<string>? scopes = null) : ICurrentUserContext
    {
        public string Subject { get; } = subject;
        public string? Email => "teacher@example.com";
        public IReadOnlyCollection<string> Scopes { get; } = scopes ?? [];

        public bool HasScope(string scope)
        {
            return Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class FakeMoodleCredentialsProvider(MoodleCredentials credentials)
        : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(credentials);
    }

    private sealed class ContextMoodleCredentialsProvider(
        IMoodleConnectionSelection selection,
        IConnectorExecutionContext executionContext)
        : IMoodleCredentialsProvider
    {
        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MoodleCredentials(
                executionContext.ClientId ?? "default",
                $"connection-{selection.Alias ?? "default"}",
                selection.Alias ?? "default",
                "https://moodle.example",
                "teacher",
                "test-only",
                "moodle",
                CanWrite: true));
    }

    private sealed class FakeMoodleGradebookGateway(
        CourseGradebook gradebook,
        List<AssignmentExistingGrade>? observedGrades = null) : IMoodleGradebookGateway
    {
        public CourseGradebook Gradebook => GetObservedGradebook();
        public GradebookReadOptions? LastReadOptions { get; private set; }
        public int ReadCalls { get; private set; }
        public Exception? ReadFailure { get; set; }

        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(GetObservedGradebook(studentId));

        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId,
            string studentId,
            GradebookReadOptions readOptions,
            CancellationToken cancellationToken)
        {
            LastReadOptions = readOptions;
            ReadCalls++;
            if (ReadFailure is not null)
            {
                return Task.FromException<CourseGradebook>(ReadFailure);
            }

            return Task.FromResult(GetObservedGradebook(studentId));
        }

        private CourseGradebook GetObservedGradebook(string? studentId = null)
        {
            var effectiveStudentId = studentId ?? gradebook.StudentId;
            if (observedGrades is null)
            {
                return gradebook with { StudentId = effectiveStudentId };
            }

            var items = gradebook.Items.Select(item =>
            {
                if (!string.Equals(item.ItemModule, "assign", StringComparison.OrdinalIgnoreCase) ||
                    item.ItemInstance is null)
                {
                    return item;
                }

                var observed = observedGrades.SingleOrDefault(grade =>
                    grade.AssignmentId == item.ItemInstance && grade.StudentId == effectiveStudentId);
                return observed is null
                    ? item
                    : item with
                    {
                        GradeRaw = observed.HasGrade ? observed.Grade : null,
                        Feedback = observed.FeedbackObserved || !string.IsNullOrWhiteSpace(observed.Feedback)
                            ? observed.Feedback
                            : item.Feedback,
                        FeedbackFieldPresent = item.FeedbackFieldPresent ||
                            observed.FeedbackObserved ||
                            !string.IsNullOrWhiteSpace(observed.Feedback)
                    };
            }).ToArray();
            return gradebook with { StudentId = effectiveStudentId, Items = items };
        }
    }

    private sealed class FakeMoodleAssignmentSettingsGateway : IMoodleAssignmentSettingsGateway
    {
        public AssignmentSettingsSummary? Result { get; set; } =
            new("501", 10m, "Atividade", IsGradable: true, RawGrade: 10m, MarkingWorkflowEnabled: false);

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken)
            => Task.FromResult(Result);
    }

    private sealed class ConnectionScopedSettingsGateway(IMoodleConnectionSelection selection)
        : IMoodleAssignmentSettingsGateway
    {
        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AssignmentSettingsSummary?>(
                string.Equals(selection.Alias, "fieg", StringComparison.Ordinal)
                    ? new AssignmentSettingsSummary(
                        assignmentId,
                        10m,
                        "Atividade",
                        IsGradable: true,
                        RawGrade: 10m,
                        MarkingWorkflowEnabled: false)
                    : null);
    }

    private sealed class FakeMoodleConnectionSelection : IMoodleConnectionSelection
    {
        public string? Alias { get; set; }
    }

    private sealed class FakeConnectorExecutionContext : IConnectorExecutionContext
    {
        public string? ClientId { get; private set; }
        public string? Subject { get; private set; }
        public string? Email { get; private set; }
        public IReadOnlyCollection<string> Scopes { get; private set; } = [];

        public void Enter(
            string clientId,
            string subject,
            string? email,
            IReadOnlyCollection<string>? scopes = null)
        {
            ClientId = clientId;
            Subject = subject;
            Email = email;
            Scopes = scopes ?? [];
        }

        public void Clear()
        {
            ClientId = null;
            Subject = null;
            Email = null;
            Scopes = [];
        }
    }

    private sealed class FakeGradingReviewRepository : IGradingReviewRepository
    {
        public List<GradingRun> Runs { get; } = [];
        public List<AssistedGradingBatch> Batches { get; } = [];
        public List<AssistedGradingItem> Items { get; } = [];
        public List<GradingEvidence> Evidence { get; } = [];
        public List<GradingContextSnapshotDocument> Snapshots { get; } = [];
        public Dictionary<Guid, IReadOnlyDictionary<Guid, string>> PublicationClaimStatuses { get; } = [];
        public int SaveChangesCallCount { get; private set; }

        public Task<IReadOnlyDictionary<Guid, string>> GetPublicationClaimStatusesAsync(
            Guid publicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                PublicationClaimStatuses.GetValueOrDefault(publicationId) ??
                new Dictionary<Guid, string>());

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task AddGradingRunAsync(GradingRun run, CancellationToken cancellationToken)
        {
            Runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<GradingRun?> GetGradingRunAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Runs.SingleOrDefault(run => run.Id == id));

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByGradingRunAsync(
            Guid gradingRunId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Batches
                .Where(batch => batch.GradingRunId == gradingRunId)
                .OrderBy(batch => batch.CreatedAt)
                .ThenBy(batch => batch.Id)
                .ToArray());

        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Batches.SingleOrDefault(batch => batch.Id == id));
        }

        public Task AddItemAsync(AssistedGradingItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return Task.CompletedTask;
        }

        public Task AddArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task AddEvidenceAsync(GradingEvidence evidence, CancellationToken cancellationToken)
        {
            Evidence.Add(evidence);
            return Task.CompletedTask;
        }

        public Task<AssistedGradingItem?> GetItemAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        }

        public Task<IReadOnlyList<AssistedGradingItem>> ListItemsByBatchAsync(
            Guid batchId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var items = Items
                .Where(item => item.BatchId == batchId)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult<IReadOnlyList<AssistedGradingItem>>(items);
        }

        public Task<int> CountItemsByBatchAsync(Guid batchId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Items.Count(item => item.BatchId == batchId));
        }

        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(
            Guid gradingItemId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<GradingArtifact>>([]);
        }

        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(
            Guid gradingItemId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<GradingEvidence>>(Evidence
                .Where(evidence => evidence.GradingItemId == gradingItemId)
                .ToArray());
        }

        public Task<IReadOnlyDictionary<Guid, GradingContextSnapshotDocument>> ListLatestContextSnapshotsByItemsAsync(
            IReadOnlyCollection<Guid> gradingItemIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, GradingContextSnapshotDocument>>(Snapshots
                .Where(snapshot => gradingItemIds.Contains(snapshot.GradingItemId))
                .GroupBy(snapshot => snapshot.GradingItemId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Version).First()));

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePendingActionService : IPendingActionService
    {
        public Guid PendingActionId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000999");
        public GradingLaunchPayload? LastPayload { get; private set; }
        public object? LastPreview { get; private set; }
        public int CreateCallCount { get; private set; }

        public Task<PendingActionResponse> CreatePendingActionAsync(
            string toolName,
            ToolRiskLevel riskLevel,
            object payload,
            object preview,
            string confirmationText,
            TimeSpan expiresIn,
            long? courseId,
            CancellationToken cancellationToken)
        {
            CreateCallCount++;
            LastPayload = Assert.IsType<GradingLaunchPayload>(payload);
            LastPreview = preview;
            return Task.FromResult(new PendingActionResponse(
                "pending_confirmation",
                PendingActionId,
                toolName,
                riskLevel,
                preview,
                confirmationText,
                DateTimeOffset.UtcNow.Add(expiresIn)));
        }
    }

    private sealed class FakePendingActionRepository : IPendingMoodleActionRepository
    {
        public List<PendingMoodleAction> Actions { get; } = [];
        public bool LoseLeaseOnRenewal { get; set; }
        public int LeaseRenewalCallCount { get; private set; }

        public Task AddAsync(PendingMoodleAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Actions.SingleOrDefault(action => action.Id == id));
        }

        public Task<IReadOnlyList<PendingMoodleAction>> ListGradingPublicationsByBatchIdAsync(
            Guid batchJobId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingMoodleAction>>(Actions.ToArray());

        public Task<IReadOnlyList<PendingMoodleAction>> ListGradingPublicationsByBatchIdsAsync(
            IReadOnlyCollection<Guid> batchJobIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingMoodleAction>>(Actions.ToArray());

        public Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(Guid id, string confirmedBySubject, DateTimeOffset confirmedAt, MoodleAuditLog confirmationAudit, CancellationToken cancellationToken)
        {
            var action = Actions.SingleOrDefault(candidate => candidate.Id == id);
            if (action?.Status != PendingActionStatus.PendingConfirmation)
                return Task.FromResult(new PendingActionConfirmationClaimResult(false, action?.Status ?? PendingActionStatus.Expired, action?.ConfirmedAt));
            action.Confirm(confirmedBySubject, confirmedAt);
            return Task.FromResult(new PendingActionConfirmationClaimResult(true, action.Status, action.ConfirmedAt));
        }

        public Task<PendingActionExecutionClaimResult> TryBeginExecutionAsync(
            Guid id,
            string workerId,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var action = Actions.SingleOrDefault(candidate => candidate.Id == id);
            if (action is null || !action.BeginExecution(workerId, now, leaseDuration))
            {
                return Task.FromResult(new PendingActionExecutionClaimResult(
                    false,
                    action?.Status ?? PendingActionStatus.Failed,
                    action?.ExecutionLeaseUntil,
                    action?.ExecutionAttemptCount ?? 0));
            }

            return Task.FromResult(new PendingActionExecutionClaimResult(
                true,
                action.Status,
                action.ExecutionLeaseUntil,
                action.ExecutionAttemptCount,
                action.ExecutionAttemptCount));
        }

        public Task<bool> TryRenewExecutionLeaseAsync(
            Guid id,
            string workerId,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            LeaseRenewalCallCount++;
            return Task.FromResult(!LoseLeaseOnRenewal &&
                Actions.SingleOrDefault(candidate => candidate.Id == id)?.ExecutionOwner == workerId);
        }

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActionConfirmationService : IActionConfirmationService
    {
        public string? LastRequiredScope { get; private set; }
        public string Status { get; set; } = "confirmed";
        public int CallCount { get; private set; }

        public Task<ActionConfirmationResponse> ConfirmAsync(
            Guid pendingActionId,
            string? confirmationText,
            string? requiredScope,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequiredScope = requiredScope;
            return Task.FromResult(new ActionConfirmationResponse(
                Status,
                pendingActionId,
                "criar_previa_lancamento_lote",
                ToolRiskLevel.CriticalHumanConfirmedWrite,
                DateTimeOffset.UtcNow,
                "audit-1"));
        }
    }

    private sealed class FakeMoodleGradingCapabilitiesGateway : IMoodleGradingCapabilitiesGateway
    {
        public List<string> Functions { get; } = ["mod_assign_save_grade"];
        public Exception? Error { get; set; }

        public Task<MoodleWebServiceFunctionCatalog> GetFunctionCatalogAsync(
            string userExternalId,
            CancellationToken cancellationToken)
        {
            if (Error is not null)
            {
                throw Error;
            }

            return Task.FromResult(new MoodleWebServiceFunctionCatalog(
                "moodle_mobile_app",
                Functions));
        }
    }

    private sealed class FakeMoodleAssignmentGradeReadGateway : IMoodleAssignmentGradeReadGateway
    {
        public List<AssignmentExistingGrade> ExistingGrades { get; } = [];
        public Exception? Error { get; set; }
        public Queue<Exception> Errors { get; } = [];
        public int CallCount { get; private set; }

        public Task<AssignmentExistingGrade?> GetExistingGradeAsync(
            string userExternalId,
            string assignmentId,
            string studentId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (Errors.TryDequeue(out var transientError))
            {
                throw transientError;
            }

            if (Error is not null)
            {
                throw Error;
            }

            return Task.FromResult(ExistingGrades.SingleOrDefault(grade =>
                grade.AssignmentId == assignmentId &&
                grade.StudentId == studentId));
        }
    }

    private sealed class FakeMoodleAssignmentSubmissionsGateway : IMoodleAssignmentSubmissionsGateway
    {
        public List<AssignmentSubmissionRecord> Submissions { get; } = [];
        public int BatchCallCount { get; private set; }

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            BatchCallCount++;
            return Task.FromResult<IReadOnlyList<AssignmentSubmissionsBatch>>(
                assignmentIds.Select(assignmentId => new AssignmentSubmissionsBatch(assignmentId, Submissions)).ToArray());
        }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssignmentSubmissionRecord>>(Submissions);
    }

    private sealed class FakeMoodleAssignmentSubmissionStatusGateway : IMoodleAssignmentSubmissionStatusGateway
    {
        public List<AssignmentSubmissionAttemptStatus> Statuses { get; } = [];
        public Queue<Exception> Errors { get; } = [];
        public int CallCount { get; private set; }

        public Task<AssignmentSubmissionAttemptStatus?> GetSubmissionStatusAsync(
            string userExternalId,
            string assignmentId,
            string studentId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (Errors.TryDequeue(out var transientError))
            {
                throw transientError;
            }

            return Task.FromResult(Statuses.LastOrDefault(status =>
                status.AssignmentId == assignmentId &&
                status.StudentId == studentId));
        }
    }

    private sealed class FakeSubmissionContentHashResolver(string hash) : ISubmissionContentHashResolver
    {
        public IReadOnlyCollection<string>? LastAttachmentHashes { get; private set; }
        public AssignmentSubmissionRecord? LastCurrentSubmission { get; private set; }
        public int CallCount { get; private set; }

        public Task<SubmissionContentHashSnapshot> ResolveAsync(string userExternalId, string assignmentId, string studentId, long submissionId, IReadOnlyCollection<string> attachmentHashes, CancellationToken cancellationToken, AssignmentSubmissionRecord? currentSubmission = null)
        {
            CallCount++;
            LastAttachmentHashes = attachmentHashes.ToArray();
            LastCurrentSubmission = currentSubmission;
            return Task.FromResult(new SubmissionContentHashSnapshot(hash, 0, DateTimeOffset.UtcNow, attachmentHashes.Count));
        }
    }

    private sealed class FakeAuditLogRepository : IMoodleAuditLogRepository
    {
        public List<MoodleAuditLog> Logs { get; } = [];

        public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken)
        {
            Logs.Add(log);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(
            string correlationId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var items = Logs
                .Where(log => log.CorrelationId == correlationId)
                .OrderBy(log => log.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult<IReadOnlyList<MoodleAuditLog>>(items);
        }

        public Task<int> CountByCorrelationIdAsync(
            string correlationId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Logs.Count(log => log.CorrelationId == correlationId));
        }

        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(
            Guid batchJobId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var items = Logs
                .Where(log => log.BatchJobId == batchJobId)
                .OrderBy(log => log.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult<IReadOnlyList<MoodleAuditLog>>(items);
        }

        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Logs.Count(log => log.BatchJobId == batchJobId));
        }

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMediator : IMediator
    {
        public List<SaveAssignmentGradeCommand> SavedGrades { get; } = [];
        public Exception? Error { get; set; }
        public Action<SaveAssignmentGradeCommand>? OnSaveGrade { get; set; }
        public Action<SaveAssignmentGradeCommand>? OnSuccessfulSaveGrade { get; set; }

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => Task.CompletedTask;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is SaveAssignmentGradeCommand save)
            {
                SavedGrades.Add(save);
                OnSaveGrade?.Invoke(save);
                if (Error is not null)
                {
                    throw Error;
                }
                OnSuccessfulSaveGrade?.Invoke(save);

                return Task.FromResult((TResponse)(object)new AssignmentGradeWriteResult(
                    true,
                    "mod_assign_save_grade",
                    "ok"));
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            if (request is SaveAssignmentGradeCommand save)
            {
                SavedGrades.Add(save);
                OnSaveGrade?.Invoke(save);
                if (Error is not null)
                {
                    throw Error;
                }
                OnSuccessfulSaveGrade?.Invoke(save);

                return Task.FromResult<object?>(new AssignmentGradeWriteResult(
                    true,
                    "mod_assign_save_grade",
                    "ok"));
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<object?>();
    }

    private sealed class FakeMoodleParticipantsGateway : IMoodleParticipantsGateway
    {
        public List<string> EnrolledUserIds { get; } = ["101"];
        public Exception? Error { get; set; }
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");

        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
            string userExternalId,
            string courseId,
            ParticipantStatusFilter statusFilter,
            int page,
            int pageSize,
            bool studentsOnly,
            bool includeEmail,
            string? groupId,
            CancellationToken cancellationToken)
        {
            if (Error is not null)
            {
                throw Error;
            }

            var participants = EnrolledUserIds
                .Select(id => new CourseParticipantSummary(
                    id,
                    $"Student {id}",
                    Email: null,
                    Suspended: false,
                    FirstAccessAt: null,
                    LastAccessAt: null,
                    LastCourseAccessAt: null,
                    Roles: [new CourseParticipantRole("5", "student", "Estudante")],
                    Groups: []))
                .ToArray();

            return Task.FromResult(new CourseParticipantsPage(
                courseId,
                page,
                pageSize,
                statusFilter,
                studentsOnly,
                includeEmail,
                HasMore: false,
                participants));
        }

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);
        }
    }
}
