using MediatR;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Courses;
using Moodle.Application.Grading;
using Moodle.Application.Submissions;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class PendingGradingRunCommandHandlerTests
{
    [Fact]
    public async Task RequeueBlockedItems_ReabreSomenteItemNaoPublicado()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.BlockAnalysis("Falha de leitura anterior.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);

        var sut = new RequeueBlockedGradingItemsCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new RequeueBlockedGradingItemsCommand(batch.Id, [item.Id]),
            CancellationToken.None);

        Assert.Equal(1, result.RequeuedItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal("queued_for_reprocessing", result.ProcessingState);
        Assert.Equal("wait", result.NextAction);
        Assert.Equal(GradingItemStatus.Pending, item.Status);
        Assert.Equal(GradingProcessingStage.Pending, item.ProcessingStage);
        Assert.Equal(GradingCommitStatus.NotReady, item.CommitStatus);

        var repeated = await sut.Handle(
            new RequeueBlockedGradingItemsCommand(batch.Id, [item.Id]),
            CancellationToken.None);

        Assert.Equal(0, repeated.RequeuedItems);
        Assert.Equal(1, repeated.AlreadyQueuedItems);
        Assert.Equal("already_queued", repeated.ProcessingState);
        Assert.Equal("wait", repeated.NextAction);
    }

    [Fact]
    public async Task RequeueFailedPublication_PreservaDecisaoFinalEReabreSomenteFalhaComprovada()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho coerente.");
        item.ApplyTeacherReview(8m, "Feedback final revisado.", "teacher-1", 321);
        item.MarkCommitFailed("A capacidade de escrita estava indisponivel.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);

        var sut = new RequeueFailedGradingPublicationItemsCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new RequeueFailedGradingPublicationItemsCommand(batch.Id, [item.Id]),
            CancellationToken.None);

        Assert.Equal(1, result.RequeuedItems);
        Assert.Equal(GradingItemStatus.ReadyToCommit, item.Status);
        Assert.Equal(GradingCommitStatus.Pending, item.CommitStatus);
        Assert.Equal(8m, item.FinalGrade);
        Assert.Equal("Feedback final revisado.", item.FinalFeedback);
    }

    [Fact]
    public async Task RequeueFailedPublication_NaoRepeteEscritaDeExecucaoDesconhecida()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho coerente.");
        item.ApplyTeacherReview(8m, "Feedback final revisado.", "teacher-1", 321);
        item.MarkCommitExecutionUnknown("Resposta perdida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);

        var sut = new RequeueFailedGradingPublicationItemsCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new RequeueFailedGradingPublicationItemsCommand(batch.Id, [item.Id]),
            CancellationToken.None);

        Assert.Equal(0, result.RequeuedItems);
        Assert.Contains(result.Failures, failure => failure.Message.Contains("desconhecida", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GradingCommitStatus.ExecutionUnknown, item.CommitStatus);
    }

    [Fact]
    public async Task FindCorrectionBySubmission_RetornaHandlesSemExporOutroProprietario()
    {
        var repository = new RunRepository();
        var ownedBatch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var ownedItem = AssistedGradingItem.Create(ownedBatch.Id, 10, 501, 9001, 101, 0);
        var otherBatch = AssistedGradingBatch.Create(10, [501], "teacher-2", 322, totalItems: 1);
        var otherItem = AssistedGradingItem.Create(otherBatch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(ownedBatch, CancellationToken.None);
        await repository.AddBatchAsync(otherBatch, CancellationToken.None);
        await repository.AddItemAsync(ownedItem, CancellationToken.None);
        await repository.AddItemAsync(otherItem, CancellationToken.None);

        var sut = new FindGradingCorrectionBySubmissionQueryHandler(
            repository,
            new RunCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new FindGradingCorrectionBySubmissionQuery(9001, 10, 501),
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.True(result.Found);
        Assert.Equal(ownedItem.Id, match.GradingItemId);
        Assert.Equal(ownedBatch.Id, match.BatchJobId);
    }

    [Fact]
    public async Task StartRun_ContinuaNosDemaisCursosQuandoUmCursoFalha()
    {
        var mediator = new RunMediator();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway());

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                UserExternalId: "321",
                MaxCourses: 10,
                MaxItemsPerBatch: 100),
            CancellationToken.None);

        var batch = Assert.Single(result.Batches);
        Assert.Equal("10", batch.CourseId);
        Assert.Equal(2, result.CoursesScanned);
        Assert.Equal(1, result.CoursesWithPendingSubmissions);
        Assert.Equal(2, result.TotalItems);
        Assert.Contains(result.Courses, course =>
            course.CourseId == "20" && course.Status == "course_read_failed");
        Assert.Contains(result.Warnings, warning => warning.Contains("Curso 20", StringComparison.Ordinal));
        Assert.Single(mediator.CreateBatchRequests);
        Assert.Equal("10", mediator.CreateBatchRequests[0].CourseId);
        Assert.NotEqual(Guid.Empty, result.GradingRunId);
        Assert.Equal(result.GradingRunId, mediator.CreateBatchRequests[0].GradingRunId);
    }

    [Fact]
    public async Task StartRun_DivideEntregasDeUmaAtividadeEmSublotesSemPerderItens()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 3);
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway());

        var result = await sut.Handle(
            new StartPendingGradingRunCommand("321", MaxCourses: 10, MaxItemsPerBatch: 2),
            CancellationToken.None);

        Assert.Equal(2, result.Batches.Count);
        Assert.Equal(3, result.TotalItems);
        Assert.All(mediator.CreateBatchRequests, request =>
            Assert.InRange(request.PrefetchedSubmissions!.Count, 1, 2));
        Assert.NotEqual(Guid.Empty, result.GradingRunId);
        Assert.All(mediator.CreateBatchRequests, request => Assert.Equal(result.GradingRunId, request.GradingRunId));
    }

    [Fact]
    public async Task StartRun_ComCursoInformado_NaoVarreOsDemaisCursos()
    {
        var mediator = new RunMediator();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway());

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 0,
                MaxItemsPerBatch: 100,
                CourseId: "10"),
            CancellationToken.None);

        Assert.Equal(["10"], mediator.RequestedCourseIds);
        Assert.Equal(1, result.CoursesDiscovered);
        Assert.Equal(1, result.CoursesScanned);
        Assert.DoesNotContain(result.Courses, course => course.CourseId == "20");
        Assert.Single(mediator.CreateBatchRequests);
        Assert.Equal("10", mediator.CreateBatchRequests[0].CourseId);
    }

    [Fact]
    public async Task StartRun_ComSubmissaoSelecionada_CriaSomenteOAlvoEUsaReservaIdempotente()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 16);
        var repository = new RunRepository();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new RunCurrentUserContext("teacher-1"));
        var request = new StartPendingGradingRunCommand(
            "321",
            MaxCourses: 0,
            MaxItemsPerBatch: 2,
            CourseId: "10",
            AssignmentIds: ["501"],
            IncludeAlreadyGraded: true,
            SubmissionIds: ["9002"],
            IdempotencyKey: "regrade-request-1",
            MoodleConnectionId: "connection-1",
            SnapshotClientId: "client-1");

        var first = await sut.Handle(request, CancellationToken.None);
        var replay = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(first.GradingRunId, replay.GradingRunId);
        Assert.Equal(1, first.TotalItems);
        Assert.Equal(1, replay.TotalItems);
        Assert.All(mediator.CreateBatchRequests, batchRequest =>
        {
            Assert.Equal(["9002"], batchRequest.SubmissionIds);
            Assert.Equal(["9002"], batchRequest.PrefetchedSubmissions!.Select(item => item.SubmissionId));
            Assert.Equal(first.GradingRunId, batchRequest.GradingRunId);
        });
        Assert.Single(repository.Runs);
    }

    [Fact]
    public async Task StartRun_ComSubmissaoSelecionada_RejeitaIDInvalidoSemCriarRunOuSublote()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 3);
        var repository = new RunRepository();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new RunCurrentUserContext("teacher-1"));

        await Assert.ThrowsAsync<ArgumentException>(() => sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 0,
                MaxItemsPerBatch: 10,
                CourseId: "10",
                AssignmentIds: ["501"],
                IncludeAlreadyGraded: true,
                SubmissionIds: ["9999"],
                IdempotencyKey: "regrade-invalid",
                MoodleConnectionId: "connection-1",
                SnapshotClientId: "client-1"),
            CancellationToken.None));

        Assert.Empty(repository.Runs);
        Assert.Empty(mediator.CreateBatchRequests);
    }

    [Fact]
    public async Task StartRun_ComSelecaoMistaValidaEInvalidaFalhaSemCriarRunParcial()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 16);
        var repository = new RunRepository();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new RunCurrentUserContext("teacher-1"));

        await Assert.ThrowsAsync<ArgumentException>(() => sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 0,
                MaxItemsPerBatch: 10,
                CourseId: "10",
                AssignmentIds: ["501"],
                IncludeAlreadyGraded: true,
                SubmissionIds: ["9002", "9999"],
                IdempotencyKey: "regrade-mixed-selection",
                MoodleConnectionId: "connection-1",
                SnapshotClientId: "client-1"),
            CancellationToken.None));

        Assert.Empty(repository.Runs);
        Assert.Empty(mediator.CreateBatchRequests);
    }

    [Fact]
    public async Task StartRun_ComMesmaChaveEDiferenteSelecao_ConflitaAntesDeCriarOutroSublote()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 3);
        var repository = new RunRepository();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new RunCurrentUserContext("teacher-1"));
        var original = new StartPendingGradingRunCommand(
            "321", 0, 10,
            CourseId: "10",
            AssignmentIds: ["501"],
            IncludeAlreadyGraded: true,
            SubmissionIds: ["9001"],
            IdempotencyKey: "regrade-same-key",
            MoodleConnectionId: "connection-1",
            SnapshotClientId: "client-1");
        await sut.Handle(original, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.Handle(
            original with { SubmissionIds = ["9002"] },
            CancellationToken.None));

        Assert.Single(repository.Runs);
        Assert.Single(mediator.CreateBatchRequests);
    }

    [Fact]
    public async Task StartRun_ComMesmaSelecaoEmOrdemDiferente_UsaOsMesmosChunksCanonicos()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 3);
        var repository = new RunRepository();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new RunCurrentUserContext("teacher-1"));
        var request = new StartPendingGradingRunCommand(
            "321", 0, 1,
            CourseId: "10",
            AssignmentIds: ["501"],
            IncludeAlreadyGraded: true,
            SubmissionIds: ["9002", "9001"],
            IdempotencyKey: "ordered-selection",
            MoodleConnectionId: "connection-1",
            SnapshotClientId: "client-1");

        var first = await sut.Handle(request, CancellationToken.None);
        var replay = await sut.Handle(request with { SubmissionIds = ["9001", "9002"] }, CancellationToken.None);

        Assert.Equal(first.GradingRunId, replay.GradingRunId);
        Assert.Equal(["9001"], mediator.CreateBatchRequests[0].SubmissionIds);
        Assert.Equal(["9002"], mediator.CreateBatchRequests[1].SubmissionIds);
        Assert.Equal(mediator.CreateBatchRequests[0].SubmissionIds, mediator.CreateBatchRequests[2].SubmissionIds);
        Assert.Equal(mediator.CreateBatchRequests[1].SubmissionIds, mediator.CreateBatchRequests[3].SubmissionIds);
    }

    [Fact]
    public async Task StartRun_ComMesmaChaveEmConexoesDiferentes_IsolaAsReservas()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 3);
        var repository = new RunRepository();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new RunCurrentUserContext("teacher-1"));
        var request = new StartPendingGradingRunCommand(
            "321", 0, 10,
            CourseId: "10",
            AssignmentIds: ["501"],
            IncludeAlreadyGraded: true,
            SubmissionIds: ["9001"],
            IdempotencyKey: "same-visible-key",
            MoodleConnectionId: "connection-1",
            SnapshotClientId: "client-1");

        var first = await sut.Handle(request, CancellationToken.None);
        var otherConnection = await sut.Handle(request with { MoodleConnectionId = "connection-2" }, CancellationToken.None);

        Assert.NotEqual(first.GradingRunId, otherConnection.GradingRunId);
        Assert.Equal(2, repository.Runs.Count);
    }

    [Fact]
    public async Task StartRun_ComIDsNumericosEquivalentes_RejeitaDuplicataAntesDaReserva()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 3);
        var repository = new RunRepository();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new RunCurrentUserContext("teacher-1"));

        await Assert.ThrowsAsync<ArgumentException>(() => sut.Handle(
            new StartPendingGradingRunCommand(
                "321", 0, 10,
                CourseId: "10",
                AssignmentIds: ["501"],
                IncludeAlreadyGraded: true,
                SubmissionIds: ["09001", "9001"],
                IdempotencyKey: "numeric-duplicate",
                MoodleConnectionId: "connection-1",
                SnapshotClientId: "client-1"),
            CancellationToken.None));

        Assert.Empty(repository.Runs);
        Assert.Empty(mediator.CreateBatchRequests);
    }

    [Fact]
    public async Task StartRun_ComNovaChavePermiteReavaliacaoIntencionalDaMesmaSelecao()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 16);
        var repository = new RunRepository();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            gradingRepository: repository,
            currentUser: new RunCurrentUserContext("teacher-1"));
        var original = new StartPendingGradingRunCommand(
            "321",
            MaxCourses: 1,
            MaxItemsPerBatch: 10,
            CourseId: "10",
            AssignmentIds: ["501"],
            IncludeAlreadyGraded: true,
            SubmissionIds: ["9002"],
            IdempotencyKey: "regrade-intent-1",
            MoodleConnectionId: "connection-1",
            SnapshotClientId: "client-1");

        var first = await sut.Handle(original, CancellationToken.None);
        var intentionalRegrade = await sut.Handle(
            original with { IdempotencyKey = "regrade-intent-2" },
            CancellationToken.None);

        Assert.NotEqual(first.GradingRunId, intentionalRegrade.GradingRunId);
        Assert.Equal(2, repository.Runs.Count);
        Assert.Equal(2, mediator.CreateBatchRequests.Count);
        Assert.All(mediator.CreateBatchRequests, request => Assert.Equal(["9002"], request.SubmissionIds));
    }

    [Fact]
    public async Task StartRun_ComAtividadesInformadas_ProcessaSomenteAsSelecionadas()
    {
        var mediator = new RunMediator();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(includeSecondAssignment: true));

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 0,
                MaxItemsPerBatch: 100,
                CourseId: "10",
                AssignmentIds: ["502"]),
            CancellationToken.None);

        Assert.Single(result.Batches);
        Assert.Equal(["502"], result.Batches[0].AssignmentIds);
        Assert.Single(mediator.CreateBatchRequests);
        Assert.Equal(["502"], mediator.CreateBatchRequests[0].AssignmentIds);
    }

    [Fact]
    public async Task StartRun_UsaLeituraBulkEIgnoraEntregaJaRevisada()
    {
        var mediator = new RunMediator();
        var submissionsGateway = new RunSubmissionGateway();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            submissionsGateway: submissionsGateway);

        var result = await sut.Handle(
            new StartPendingGradingRunCommand("321", MaxCourses: 0, MaxItemsPerBatch: 400, CourseId: "10"),
            CancellationToken.None);

        Assert.Equal(1, submissionsGateway.BatchCalls);
        Assert.Equal(1, result.TotalItems);
        var request = Assert.Single(mediator.CreateBatchRequests);
        var prefetched = request.PrefetchedSubmissions!;
        Assert.Single(prefetched);
        Assert.Equal("submission-pending", prefetched[0].SubmissionId);
    }

    [Fact]
    public async Task StartRun_PreservaResultadosSaudaveisQuandoUmaAtividadeFalhaNoBulk()
    {
        var healthyAssignmentIds = Enumerable.Range(501, 49).Select(id => id.ToString()).ToArray();
        var failedAssignmentId = "550";
        string[] assignmentIds = [.. healthyAssignmentIds, failedAssignmentId];
        var mediator = new RunMediator(pendingSubmissionCount: 1);
        var submissionsGateway = new RunSubmissionGateway
        {
            BatchResults = assignmentIds.Select(assignmentId => assignmentId == failedAssignmentId
                ? new AssignmentSubmissionsBatch(assignmentId, [], "network_error", "sensitive response body")
                : new AssignmentSubmissionsBatch(assignmentId, [])).ToArray()
        };
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(assignmentIds: assignmentIds),
            submissionsGateway: submissionsGateway);

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 1,
                MaxItemsPerBatch: 100,
                CourseId: "10"),
            CancellationToken.None);

        Assert.Equal(failedAssignmentId, Assert.Single(mediator.RequestedAssignmentIds));
        Assert.DoesNotContain(healthyAssignmentIds, id => mediator.RequestedAssignmentIds.Contains(id));
        Assert.Single(result.Batches);
        Assert.Contains(result.Warnings, warning => warning.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("sensitive response body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartRun_AtividadeValidamenteVaziaNoBulkNaoUsaFallback()
    {
        var mediator = new RunMediator();
        var submissionsGateway = new RunSubmissionGateway
        {
            BatchResults = [new AssignmentSubmissionsBatch("501", [])]
        };
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            submissionsGateway: submissionsGateway);

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 1,
                MaxItemsPerBatch: 100,
                CourseId: "10"),
            CancellationToken.None);

        Assert.Empty(mediator.RequestedAssignmentIds);
        Assert.Empty(result.Batches);
        Assert.True(result.TotalItems is 0);
    }

    [Fact]
    public async Task StartRun_ReavaliacaoExplicitaIncluiEntregaJaRevisada()
    {
        var mediator = new RunMediator();
        var submissionsGateway = new RunSubmissionGateway();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            submissionsGateway: submissionsGateway);

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 0,
                MaxItemsPerBatch: 400,
                CourseId: "10",
                IncludeAlreadyGraded: true),
            CancellationToken.None);

        Assert.Equal(2, result.TotalItems);
        Assert.Null(submissionsGateway.LastStatus);
        var request = Assert.Single(mediator.CreateBatchRequests);
        Assert.True(request.IncludeAlreadyGraded);
        Assert.False(request.OnlyAwaitingGrading);
        Assert.Equal(
            ["submission-pending", "submission-reviewed"],
            request.PrefetchedSubmissions!.Select(submission => submission.SubmissionId!).ToArray());
        Assert.Contains(result.Warnings, warning => warning.Contains("reavaliacao", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartRun_UsaSnapshotsSemLerCursosOuEntregasNoMoodle()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 99);
        var ownerId = Guid.NewGuid();
        var course = new CourseSummary(
            "10",
            IdNumber: null,
            ShortName: "Curso snapshot",
            FullName: "Curso snapshot",
            DisplayName: "Curso snapshot",
            CategoryId: null,
            CategoryName: null,
            StartDate: null,
            EndDate: null,
            Visible: true,
            ViewUrl: null,
            CourseImage: null,
            Progress: null,
            HasProgress: null,
            IsFavourite: null,
            LastAccessAt: null);
        var submission = new AssignmentSubmissionSummary(
            "student-1",
            "Aluno Snapshot",
            "submission-1",
            "submitted",
            "notgraded",
            Submitted: true,
            Late: false,
            NeedsGrading: true,
            SubmittedAt: DateTimeOffset.UtcNow,
            ModifiedAt: DateTimeOffset.UtcNow,
            AttemptNumber: 1,
            FileCount: 1,
            HasOnlineText: false,
            Files: []);
        var snapshot = new CourseAssignmentSubmissionsSnapshot(
            "10",
            [new AssignmentSubmissionsSnapshotItem(
                "501",
                "1001",
                "Atividade snapshot",
                null,
                [submission])]);
        var store = new RunSnapshotStore(
            [course],
            new MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>(
                snapshot,
                DateTimeOffset.UtcNow,
                IsStale: false,
                IsFrozen: false,
                Tier: "hot",
                IsComplete: true,
                RecordCount: 1));
        var queue = new RunSnapshotQueue();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new ThrowingCourseContentsGateway(),
            store,
            queue);

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 10,
                MaxItemsPerBatch: 100,
                UseSubmissionSnapshots: true,
                SnapshotOwnerId: ownerId,
                SnapshotClientId: "client-1",
                SnapshotConnectionAlias: "goias"),
            CancellationToken.None);

        Assert.Equal(1, result.TotalItems);
        Assert.Single(result.Batches);
        Assert.Single(mediator.CreateBatchRequests);
        Assert.Equal("501", Assert.Single(mediator.CreateBatchRequests[0].AssignmentIds));
        Assert.Equal("submission-1", Assert.Single(mediator.CreateBatchRequests[0].PrefetchedSubmissions!).SubmissionId);
        Assert.Empty(queue.Requests);
    }

    [Fact]
    public async Task StartRun_ConservaReferenciasQuandoEntregaJaEstaEmCorrecaoAnterior()
    {
        var existingCorrection = new ExistingGradingCorrectionReference(
            SubmissionId: 9001,
            GradingItemId: Guid.NewGuid(),
            BatchJobId: Guid.NewGuid(),
            GradingRunId: Guid.NewGuid(),
            ItemStatus: nameof(GradingItemStatus.AwaitingAiAnalysis),
            CommitStatus: nameof(GradingCommitStatus.NotReady),
            BatchStatus: nameof(GradingBatchStatus.Processing),
            RunStatus: nameof(GradingRunStatus.Ready));
        var mediator = new RunMediator { ReturnAlreadyQueued = true, ExistingCorrection = existingCorrection };
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway());

        var result = await sut.Handle(
            new StartPendingGradingRunCommand("321", MaxCourses: 10, MaxItemsPerBatch: 100),
            CancellationToken.None);

        Assert.Empty(result.Batches);
        Assert.Single(result.ExistingCorrections);
        Assert.Equal(existingCorrection.GradingItemId, result.ExistingCorrections[0].GradingItemId);
        Assert.Contains(result.Courses, course => course.Status == "already_queued");
        Assert.Contains(result.Warnings, warning => warning.Contains("correção", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartRun_UsaFallbackIndividualQuandoRespostaEmLoteOmiteAtividade()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 1);
        var submissionsGateway = new RunSubmissionGateway { OmitBatchResult = true };
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new RunCourseContentsGateway(),
            submissionsGateway: submissionsGateway);

        var result = await sut.Handle(
            new StartPendingGradingRunCommand("321", MaxCourses: 10, MaxItemsPerBatch: 100),
            CancellationToken.None);

        Assert.Single(result.Batches);
        Assert.Contains(result.Warnings, warning => warning.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        Assert.Single(mediator.CreateBatchRequests);
    }

    [Fact]
    public async Task StartRun_ComAtividadeInformadaFiltraTambemNoCaminhoPorSnapshot()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 99);
        var ownerId = Guid.NewGuid();
        var course = new CourseSummary(
            "10",
            IdNumber: null,
            ShortName: "Curso snapshot",
            FullName: "Curso snapshot",
            DisplayName: "Curso snapshot",
            CategoryId: null,
            CategoryName: null,
            StartDate: null,
            EndDate: null,
            Visible: true,
            ViewUrl: null,
            CourseImage: null,
            Progress: null,
            HasProgress: null,
            IsFavourite: null,
            LastAccessAt: null);
        var submission = new AssignmentSubmissionSummary(
            "student-1",
            "Aluno Snapshot",
            "submission-1",
            "submitted",
            "notgraded",
            Submitted: true,
            Late: false,
            NeedsGrading: true,
            SubmittedAt: DateTimeOffset.UtcNow,
            ModifiedAt: DateTimeOffset.UtcNow,
            AttemptNumber: 1,
            FileCount: 1,
            HasOnlineText: false,
            Files: []);
        var snapshot = new CourseAssignmentSubmissionsSnapshot(
            "10",
            [
                new AssignmentSubmissionsSnapshotItem("501", "1001", "Outra atividade", null, [submission]),
                new AssignmentSubmissionsSnapshotItem("502", "1002", "Atividade selecionada", null, [submission])
            ]);
        var store = new RunSnapshotStore(
            [course],
            new MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>(
                snapshot,
                DateTimeOffset.UtcNow,
                IsStale: false,
                IsFrozen: false,
                Tier: "hot",
                IsComplete: true,
                RecordCount: 2));
        var queue = new RunSnapshotQueue();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new ThrowingCourseContentsGateway(),
            store,
            queue);

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 10,
                MaxItemsPerBatch: 100,
                UseSubmissionSnapshots: true,
                SnapshotOwnerId: ownerId,
                SnapshotClientId: "client-1",
                SnapshotConnectionAlias: "goias",
                AssignmentIds: ["502"]),
            CancellationToken.None);

        var batch = Assert.Single(result.Batches);
        Assert.Equal(["502"], batch.AssignmentIds);
        Assert.Single(mediator.CreateBatchRequests);
        Assert.Equal(["502"], mediator.CreateBatchRequests[0].AssignmentIds);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("501", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartRun_NaoConverteSnapshotIncompletoEmNenhumaPendencia()
    {
        var mediator = new RunMediator(pendingSubmissionCount: 99);
        var ownerId = Guid.NewGuid();
        var course = new CourseSummary(
            "10",
            IdNumber: null,
            ShortName: "Curso snapshot",
            FullName: "Curso snapshot",
            DisplayName: "Curso snapshot",
            CategoryId: null,
            CategoryName: null,
            StartDate: null,
            EndDate: null,
            Visible: true,
            ViewUrl: null,
            CourseImage: null,
            Progress: null,
            HasProgress: null,
            IsFavourite: null,
            LastAccessAt: null);
        var store = new RunSnapshotStore(
            [course],
            new MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>(
                new CourseAssignmentSubmissionsSnapshot("10", []),
                DateTimeOffset.UtcNow,
                IsStale: false,
                IsFrozen: false,
                Tier: "hot",
                IsComplete: false,
                RecordCount: 0));
        var queue = new RunSnapshotQueue();
        var sut = new StartPendingGradingRunCommandHandler(
            mediator,
            new ThrowingCourseContentsGateway(),
            store,
            queue);

        var result = await sut.Handle(
            new StartPendingGradingRunCommand(
                "321",
                MaxCourses: 10,
                MaxItemsPerBatch: 100,
                UseSubmissionSnapshots: true,
                SnapshotOwnerId: ownerId,
                SnapshotClientId: "client-1",
                SnapshotConnectionAlias: "goias",
                AssignmentIds: ["502"]),
            CancellationToken.None);

        Assert.Equal("partial_failure", Assert.Single(result.Courses).Status);
        Assert.Contains(result.Warnings, warning => warning.Contains("incompleto", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queue.Requests, request =>
            request.Dataset == MoodleSnapshotDatasets.Submissions && request.CourseId == "10");
    }

    [Fact]
    public async Task GetRunReport_ListaCorrigidosENaoCorrigidosComMotivo()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var corrected = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        corrected.SetDraft(9m, 0.9m, "Rascunho aprovado.");
        corrected.ApplyTeacherReview(9m, "Feedback final.", "teacher-1", 321);
        corrected.MarkCommitSucceeded();
        var unreadable = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        unreadable.BlockAnalysis("Submissao sem conteudo legivel: arquivo PDF corrompido.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(corrected, CancellationToken.None);
        await repository.AddItemAsync(unreadable, CancellationToken.None);

        var sut = new GetPendingGradingRunReportQueryHandler(
            repository,
            new RunCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetPendingGradingRunReportQuery([batch.Id]),
            CancellationToken.None);

        Assert.Equal(2, result.TotalItems);
        Assert.Equal(1, result.CorrectedCount);
        Assert.Equal(1, result.NotCorrectedCount);
        Assert.Equal(corrected.Id, Assert.Single(result.CorrectedItems).GradingItemId);
        var blocked = Assert.Single(result.NotCorrectedItems);
        Assert.Equal(unreadable.Id, blocked.GradingItemId);
        Assert.Contains("arquivo PDF corrompido", blocked.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Corrigidas e lancadas no Moodle", result.ReportMarkdown, StringComparison.Ordinal);
        Assert.Contains("Nao corrigidas para ajuste manual", result.ReportMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAiBatch_MantemBloqueadosForaDaCorrecaoEmCadeia()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var blocked = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        blocked.BlockAnalysis("Arquivo enviado esta corrompido e nao pode ser lido.");
        var eligible = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        eligible.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(blocked, CancellationToken.None);
        await repository.AddItemAsync(eligible, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(),
                eligible.Id,
                "submission_file",
                "entrega.pdf",
                "application/pdf",
                null,
                12,
                ExtractionStatus.Pending,
                null,
                "pending_resource",
                DateTimeOffset.UtcNow,
                "https://moodle.example/pluginfile.php/1/entrega.pdf"),
            CancellationToken.None);
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway());

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [
                    new AiGradingItemInput(blocked.Id, "Aluno bloqueado", 8m, "Feedback que deve ser ignorado."),
                    new AiGradingItemInput(eligible.Id, "Aluno apto", 9m, "Feedback gerado para revisao.")
                ]),
            CancellationToken.None);

        Assert.Equal(1, result.SavedItems);
        Assert.Equal(1, result.SkippedItems);
        Assert.Equal(GradingItemStatus.Blocked, blocked.Status);
        Assert.Contains("corrompido", blocked.DraftFeedback, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GradingItemStatus.DraftReady, eligible.Status);
        Assert.Equal(0m, eligible.Confidence);
        Assert.DoesNotContain("Aluno apto", eligible.DraftFeedback, StringComparison.Ordinal);
        Assert.DoesNotContain("Aluno apto", eligible.PrivateNotesToTeacher, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAiBatch_RecalculaSomenteContadoresDosSublotesComItensNoPedido()
    {
        var repository = new RunRepository();
        var run = GradingRun.Create("teacher-1");
        repository.AddRun(run);

        AssistedGradingBatch CreateChildBatch() => AssistedGradingBatch.Create(
            10, [501], "teacher-1", 321, totalItems: 1, gradingRunId: run.Id);

        var firstBatch = CreateChildBatch();
        var untouchedBatch = CreateChildBatch();
        var thirdBatch = CreateChildBatch();
        var outsideBatch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var firstItem = AssistedGradingItem.Create(firstBatch.Id, 10, 501, 9001, 101, 0);
        var untouchedItem = AssistedGradingItem.Create(untouchedBatch.Id, 10, 501, 9002, 102, 0);
        var thirdItem = AssistedGradingItem.Create(thirdBatch.Id, 10, 501, 9003, 103, 0);
        var outsideItem = AssistedGradingItem.Create(outsideBatch.Id, 10, 501, 9004, 104, 0);
        firstItem.MarkAwaitingAiAnalysis("Contexto validado.");
        untouchedItem.BlockAnalysis("Falha tecnica anterior.");
        thirdItem.MarkAwaitingAiAnalysis("Contexto validado.");
        outsideItem.MarkAwaitingAiAnalysis("Contexto validado.");
        untouchedBatch.UpdateCounters(processedItems: 1, readyItems: 0, blockedItems: 1, failedItems: 0);

        await repository.AddBatchAsync(firstBatch, CancellationToken.None);
        await repository.AddBatchAsync(untouchedBatch, CancellationToken.None);
        await repository.AddBatchAsync(thirdBatch, CancellationToken.None);
        await repository.AddBatchAsync(outsideBatch, CancellationToken.None);
        await repository.AddItemAsync(firstItem, CancellationToken.None);
        await repository.AddItemAsync(untouchedItem, CancellationToken.None);
        await repository.AddItemAsync(thirdItem, CancellationToken.None);
        await repository.AddItemAsync(outsideItem, CancellationToken.None);
        var previousUntouchedCounters = new
        {
            untouchedBatch.ProcessedItems,
            untouchedBatch.ReadyItems,
            untouchedBatch.BlockedItems,
            untouchedBatch.FailedItems,
            untouchedBatch.Status
        };
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway());

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                run.Id,
                [
                    new AiGradingItemInput(firstItem.Id, "Aluno 1", null, "Feedback do item 1."),
                    new AiGradingItemInput(thirdItem.Id, "Aluno 3", null, "Feedback do item 3."),
                    new AiGradingItemInput(outsideItem.Id, "Aluno fora do escopo", null, "Feedback fora do escopo.")
                ]),
            CancellationToken.None);

        Assert.Equal(2, result.SavedItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Contains(firstBatch.Id, repository.ListItemsByBatchCalls);
        Assert.DoesNotContain(untouchedBatch.Id, repository.ListItemsByBatchCalls);
        Assert.Contains(thirdBatch.Id, repository.ListItemsByBatchCalls);
        Assert.DoesNotContain(outsideBatch.Id, repository.ListItemsByBatchCalls);
        Assert.Equal(1, firstBatch.ProcessedItems);
        Assert.Equal(1, firstBatch.ReadyItems);
        Assert.Equal(GradingBatchStatus.ReadyForReview, firstBatch.Status);
        Assert.Equal(1, thirdBatch.ProcessedItems);
        Assert.Equal(1, thirdBatch.ReadyItems);
        Assert.Equal(GradingBatchStatus.ReadyForReview, thirdBatch.Status);
        Assert.Equal(previousUntouchedCounters, new
        {
            untouchedBatch.ProcessedItems,
            untouchedBatch.ReadyItems,
            untouchedBatch.BlockedItems,
            untouchedBatch.FailedItems,
            untouchedBatch.Status
        });
        Assert.Equal(GradingBatchStatus.Pending, outsideBatch.Status);
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, outsideItem.Status);
    }

    [Fact]
    public async Task SaveAiBatch_Pagina400ItensSomenteNoSubloteTocado()
    {
        const int batchCount = 25;
        const int itemsPerBatch = 400;
        var repository = new RunRepository();
        var run = GradingRun.Create("teacher-1");
        repository.AddRun(run);
        var batches = Enumerable.Range(0, batchCount)
            .Select(_ => AssistedGradingBatch.Create(
                10, [501], "teacher-1", 321, totalItems: itemsPerBatch, gradingRunId: run.Id))
            .ToArray();
        foreach (var batch in batches)
        {
            await repository.AddBatchAsync(batch, CancellationToken.None);
        }

        AssistedGradingItem? submittedItem = null;
        long submissionId = 100_000;
        long moodleUserId = 200_000;
        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            for (var itemIndex = 0; itemIndex < itemsPerBatch; itemIndex++)
            {
                var item = AssistedGradingItem.Create(
                    batches[batchIndex].Id,
                    10,
                    501,
                    submissionId++,
                    moodleUserId++,
                    0);
                if (batchIndex == 0 && itemIndex == 0)
                {
                    item.MarkAwaitingAiAnalysis("Contexto sintetico validado.");
                    submittedItem = item;
                }
                await repository.AddItemAsync(item, CancellationToken.None);
            }
        }

        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway());
        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                run.Id,
                [new AiGradingItemInput(
                    submittedItem!.Id,
                    "Aluno sintetico",
                    null,
                    "Feedback sintetico para revisao.")]),
            CancellationToken.None);

        Assert.Equal(1, result.SavedItems);
        Assert.Equal(5, repository.ListItemsByBatchCalls.Count);
        Assert.All(repository.ListItemsByBatchCalls, batchId => Assert.Equal(batches[0].Id, batchId));
        Assert.Equal(1, batches[0].ProcessedItems);
        Assert.Equal(1, batches[0].ReadyItems);
        Assert.All(batches.Skip(1), batch => Assert.Equal(0, batch.ProcessedItems));
    }

    [Fact]
    public async Task SaveAiBatch_RejeitaNotaNegativaSemAlterarItem()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway());

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(item.Id, "Nome legado ignorado", -0.01m, "Feedback valido.")]),
            CancellationToken.None);

        Assert.Equal(0, result.SavedItems);
        Assert.Equal(1, result.SkippedItems);
        Assert.Contains(result.Warnings, warning => warning.Contains("negativa", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, item.Status);
        Assert.Null(item.SuggestedGrade);
        Assert.Null(item.Confidence);
    }

    [Fact]
    public async Task SaveAiBatch_NaoSobrescreveRascunhoQuandoVersaoEsperadaMudou()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        var expectedDraftVersionHash = GradingDraftVersionHash.Compute(item);
        item.SetDraft(7m, 0m, "Revisao humana ja iniciada.", maxGrade: 10m);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway(maxGrade: 10m));

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(item.Id, "Aluno", 9m, "Resultado do pacote.")],
                ExpectedDraftVersionHashes: new Dictionary<Guid, string>
                {
                    [item.Id] = expectedDraftVersionHash
                }),
            CancellationToken.None);

        Assert.Equal(0, result.SavedItems);
        Assert.Equal(1, result.SkippedItems);
        Assert.Contains(result.Warnings, warning => warning.Contains("versao", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(7m, item.SuggestedGrade);
        Assert.Equal("Revisao humana ja iniciada.", item.DraftFeedback);
    }

    [Fact]
    public async Task SaveAiBatch_RejeitaNotaAcimaDaEscalaConfirmadaSemAlterarItem()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway(maxGrade: 10m));

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(item.Id, "Nome legado ignorado", 10.01m, "Feedback valido.")]),
            CancellationToken.None);

        Assert.Equal(0, result.SavedItems);
        Assert.Equal(1, result.SkippedItems);
        Assert.Contains(result.Warnings, warning => warning.Contains("escala maxima", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, item.Status);
        Assert.Null(item.SuggestedGrade);
    }

    [Fact]
    public async Task SaveAiBatch_PublicaPropostaVersionadaVinculadaAoContexto()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        var activeRubric = new GradingRubricSnapshot(
            "Rubrica oficial",
            "moodle:active",
            DefinitionId: 77,
            CourseModuleId: 8801,
            AreaName: "submissions",
            Method: "rubric",
            AssociationStatus: "active",
            DefinitionVersion: "rubric-v1",
            MinimumPoints: 0m,
            MaximumPoints: 10m,
            Criteria:
            [
                new GradingRubricCriterionSnapshot(
                    "C1",
                    "Criterio",
                    0m,
                    10m,
                    [
                        new GradingRubricLevelSnapshot("0", 0m, "Insuficiente"),
                        new GradingRubricLevelSnapshot("8", 8m, "Bom"),
                        new GradingRubricLevelSnapshot("10", 10m, "Completo")
                    ])
            ]);
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            "9001",
            "101",
            "Enunciado",
            "Criterio",
            null,
            10m,
            null,
            "Texto",
            [],
            null,
            null,
            gradingMode: "numeric",
            confirmedMinimumGrade: 0m,
            confirmedMaximumGrade: 10m,
            gradeScaleSource: "assignment_settings",
            gradeScaleVersion: "scale-v1",
            formalRubric: activeRubric,
            courseModuleId: 8801);
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        repository.AddSnapshot(snapshot);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var proposalStore = new CapturingProposalStore();
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway(maxGrade: 10m),
            proposalStore);

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(
                    item.Id,
                    null,
                    null,
                    "Feedback estruturado",
                    new AiGradingProposalInput(
                        1,
                        item.ContextHash,
                        8m,
                        "Feedback estruturado",
                        [new AiGradingCriterionInput("C1", "Criterio", 10m, 8m, AiGradingCriterionSource.FormalRubric, "Evidencia", null, false, false, [])],
                        [],
                        [],
                        [],
                        .9m,
                        new GradingExtractionSummary("succeeded", 1, false, 5, 5, null),
                        new GradingEvidenceCoverage(1, 1, 1, 1, 5, 5, false)))]),
            CancellationToken.None);

        Assert.True(result.SavedItems == 1, string.Join(" | ", result.Warnings));
        var proposal = Assert.Single(proposalStore.Proposals);
        Assert.Equal(item.ContextHash, proposal.ContextHash);
        Assert.Equal(8m, proposal.SuggestedGrade);
        Assert.True(proposal.ReviewRequired);
        Assert.Equal(GradingItemStatus.DraftReady, item.Status);
        Assert.Equal(.9m, item.Confidence);
    }

    [Fact]
    public async Task SaveAiBatch_ComOrigemInternaDoPacotePersisteHashDaEntrega()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        item.RecordSubmissionIntegrity(new string('c', 64), []);
        var context = GradingContext.Build(
            item.Id, batch.Id, "10", "501", "9001", "101", "Enunciado", "Criterio", null,
            10m, null, "Texto", [], null, null,
            gradingMode: "numeric",
            confirmedMaximumGrade: 10m,
            gradeScaleSource: "assignment_settings",
            gradeScaleVersion: "scale-v1");
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var proposalStore = new CapturingProposalStore();
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway(maxGrade: 10m),
            proposalStore);

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(
                    item.Id,
                    null,
                    8m,
                    "Feedback do pacote",
                    new AiGradingProposalInput(
                        1,
                        item.ContextHash,
                        8m,
                        "Feedback do pacote",
                        [],
                        [new AiGradingEvidenceInput(Guid.NewGuid(), "Entrega observada", null)],
                        [],
                        [],
                        null,
                        null,
                        null,
                        "graded",
                        "grading_package",
                        new string('c', 64)))],
                TrustedPackageImport: true),
            CancellationToken.None);

        Assert.Equal(1, result.SavedItems);
        Assert.Equal(new string('c', 64), Assert.Single(proposalStore.Proposals).SubmissionContentHash);
    }

    [Fact]
    public async Task SaveAiBatch_ComDraftMcpSelaSomenteAnexosDaSubmissao()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        var context = GradingContext.Build(
            item.Id, batch.Id, "10", "501", "9001", "101", "Enunciado", "Criterio", null,
            10m, null, "Texto", [], null, null,
            gradingMode: "numeric",
            confirmedMaximumGrade: 10m,
            gradeScaleSource: "assignment_settings",
            gradeScaleVersion: "scale-v1");
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.AddSnapshot(snapshot);

        const string submissionResourceId = "0123456789abcdef0123456789abcdef";
        const string contextResourceId = "fedcba9876543210fedcba9876543210";
        var resources = new RunMoodleResourceRepository([
            new MoodleResource
            {
                ResourceId = submissionResourceId,
                ResourceType = "submission_attachment",
                SubmissionId = 9001,
                Sha256 = new string('a', 64),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            },
            new MoodleResource
            {
                ResourceId = contextResourceId,
                ResourceType = "assignment_context_attachment",
                CourseId = 10,
                AssignmentId = 501,
                Sha256 = new string('b', 64),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            }
        ]);
        var proposalStore = new CapturingProposalStore();
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway(maxGrade: 10m),
            proposalStore,
            resourceRepository: resources,
            submissionContentHashResolver: new RunSubmissionContentHashResolver(new string('c', 64)),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingDraftEnabled = true
            }));

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(
                    item.Id,
                    null,
                    null,
                    "Feedback estruturado",
                    new AiGradingProposalInput(
                        1,
                        item.ContextHash,
                        8m,
                        "Feedback estruturado",
                        [],
                        [new AiGradingEvidenceInput(Guid.NewGuid(), "Material de contexto", null, $"moodle://resource/{contextResourceId}")],
                        [$"moodle://resource/{submissionResourceId}"],
                        [],
                        .9m,
                        new GradingExtractionSummary("succeeded", 1, false, 5, 5, null),
                        new GradingEvidenceCoverage(1, 1, 1, 1, 5, 5, false)))]),
            CancellationToken.None);

        Assert.Equal(1, result.SavedItems);
        Assert.Equal(new string('c', 64), item.SubmissionContentHash);
        Assert.Equal([submissionResourceId], item.GetSubmissionResourceIds());
        Assert.Equal([$"moodle://resource/{submissionResourceId}"], Assert.Single(proposalStore.Proposals).ResourceUris);
    }

    [Fact]
    public async Task SaveAiBatch_NaoAceitaOrigemDePacoteInformadaPeloCliente()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        var context = GradingContext.Build(
            item.Id, batch.Id, "10", "501", "9001", "101", "Enunciado", "Criterio", null,
            10m, null, "Texto", [], null, null,
            gradingMode: "numeric",
            confirmedMaximumGrade: 10m,
            gradeScaleSource: "assignment_settings",
            gradeScaleVersion: "scale-v1");
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.AddSnapshot(snapshot);
        var artifactId = Guid.NewGuid();
        await repository.AddArtifactAsync(
            new GradingArtifact(
                artifactId,
                item.Id,
                "submission_file",
                "entrega.pdf",
                "application/pdf",
                new string('a', 64),
                10,
                ExtractionStatus.Succeeded,
                "evidencia",
                null,
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway(maxGrade: 10m),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingDraftEnabled = true
            }));

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(
                    item.Id,
                    null,
                    null,
                    "Feedback estruturado",
                    new AiGradingProposalInput(
                        1,
                        item.ContextHash,
                        8m,
                        "Feedback estruturado",
                        [],
                        [new AiGradingEvidenceInput(artifactId, "Evidencia do pacote", null)],
                        [],
                        [],
                        .9m,
                        new GradingExtractionSummary("succeeded", 1, false, 5, 5, null),
                        new GradingEvidenceCoverage(1, 1, 1, 1, 5, 5, false),
                        Source: "grading_package"))]),
            CancellationToken.None);

        Assert.Equal(0, result.SavedItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, item.Status);
        Assert.Contains(result.Warnings, warning => warning.Contains("resources", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveAiBatch_ComAvisoTecnicoMantemDraftEAsResourceUrisOriginais()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        var context = GradingContext.Build(
            item.Id, batch.Id, "10", "501", "9001", "101", "Enunciado", "Criterio", null,
            10m, null, "Texto", [], null, null,
            gradingMode: "numeric",
            confirmedMaximumGrade: 10m,
            gradeScaleSource: "assignment_settings",
            gradeScaleVersion: "scale-v1");
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.AddSnapshot(snapshot);

        const string resourceId = "0123456789abcdef0123456789abcdef";
        var resourceUri = $"moodle://resource/{resourceId}";
        var resources = new RunMoodleResourceRepository([
            new MoodleResource
            {
                ResourceId = resourceId,
                ResourceType = "submission_attachment",
                SubmissionId = 9001,
                Sha256 = null,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            }
        ]);
        var proposalStore = new CapturingProposalStore();
        var sut = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway(maxGrade: 10m),
            proposalStore,
            resourceRepository: resources,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpGradingDraftEnabled = true,
                McpGradingSecurityWarningsOnly = true
            }));

        var result = await sut.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(
                    item.Id,
                    null,
                    null,
                    "Feedback estruturado",
                    new AiGradingProposalInput(
                        1,
                        item.ContextHash,
                        8m,
                        "Feedback estruturado",
                        [],
                        [new AiGradingEvidenceInput(Guid.NewGuid(), "Arquivo enviado", null, resourceUri)],
                        [resourceUri],
                        [],
                        .9m,
                        new GradingExtractionSummary("succeeded", 1, false, 5, 5, null),
                        new GradingEvidenceCoverage(1, 1, 1, 1, 5, 5, false)))]),
            CancellationToken.None);

        Assert.Equal(1, result.SavedItems);
        Assert.Equal(0, result.SkippedItems);
        Assert.Contains(result.Warnings, warning => warning.Contains("nao possuem hash", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GradingItemStatus.DraftReady, item.Status);
        Assert.Null(item.SubmissionContentHash);
        Assert.Equal([resourceUri], Assert.Single(proposalStore.Proposals).ResourceUris);
        Assert.Contains("Avisos tecnicos nao bloqueantes", item.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareAiBatch_ExcluiItensBloqueadosDoPacoteDeCorrecao()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var blocked = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        blocked.BlockAnalysis("Arquivo sem conteudo legivel.");
        var eligible = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        eligible.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(blocked, CancellationToken.None);
        await repository.AddItemAsync(eligible, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(), eligible.Id, "submission_file", "entrega.pdf", "application/pdf",
                null, 12, ExtractionStatus.Pending, null, "pending_resource", DateTimeOffset.UtcNow,
                "https://moodle.example/pluginfile.php/1/entrega.pdf"),
            CancellationToken.None);
        var sut = new PrepareAiGradingBatchQueryHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunAssignmentSettingsGateway(),
            resourceGateway: new RunResourceGateway(),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));

        var result = await sut.Handle(new PrepareAiGradingBatchQuery(batch.Id), CancellationToken.None);

        Assert.Equal(1, result.TotalItems);
        Assert.Equal(eligible.Id, Assert.Single(result.Items).GradingItemId);
        Assert.Contains(result.Warnings, warning => warning.Contains("bloqueado", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("EVIDENCIA NAO CONFIAVEL", result.Instructions, StringComparison.Ordinal);
        Assert.Contains("possivel_prompt_injection", result.Instructions, StringComparison.Ordinal);
        Assert.Contains("studentId", result.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("cumprimente o aluno pelo nome", result.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FeedbackOnly_PrepareAndSave_PreserveNullGradeAndExplicitMode()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        var context = GradingContext.Build(
            item.Id,
            batch.Id,
            "10",
            "501",
            "9001",
            "101",
            "Enunciado da atividade",
            "Criterio qualitativo",
            rubricDescription: null,
            maxGrade: null,
            gradeScale: null,
            submissionText: "Texto entregue pelo estudante.",
            attachedFiles: [],
            courseMaterials: null,
            teacherInstructions: null,
            gradingMode: "feedback_only");
        var snapshot = GradingContextSnapshotFactory.Create(item, context, new GradingContextOptions());
        item.RecordContextSnapshot(snapshot);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "submission_file",
                "entrega.txt",
                "text/plain",
                null,
                31,
                ExtractionStatus.Pending,
                null,
                "pending_resource",
                DateTimeOffset.UtcNow,
                "https://moodle.example/pluginfile.php/1/entrega.txt"),
            CancellationToken.None);
        repository.AddSnapshot(snapshot);

        var prepare = new PrepareAiGradingBatchQueryHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunAssignmentSettingsGateway(maxGrade: 0m),
            resourceGateway: new RunResourceGateway(),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));
        var package = await prepare.Handle(new PrepareAiGradingBatchQuery(batch.Id), CancellationToken.None);

        var packageItem = Assert.Single(package.Items);
        Assert.Null(packageItem.MaxGrade);
        Assert.False(packageItem.IsGradable);
        Assert.Equal("feedback_only", packageItem.GradingMode);

        var save = new SaveAiGradingBatchCommandHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunMoodleUserResolver(),
            new RunAuditLogRepository(),
            new RunAssignmentSettingsGateway(maxGrade: 0m));
        var saved = await save.Handle(
            new SaveAiGradingBatchCommand(
                batch.Id,
                [new AiGradingItemInput(
                    item.Id,
                    Nome: null,
                    Nota: null,
                    Feedback: "Feedback qualitativo gerado pela IA.")]),
            CancellationToken.None);

        Assert.Equal(1, saved.SavedItems);
        Assert.Null(item.SuggestedGrade);
        Assert.Equal(GradingItemStatus.DraftReady, item.Status);
        Assert.Null(Assert.Single(saved.UpdatedItems!).SuggestedGrade);
    }

    [Fact]
    public async Task PrepareAiBatch_UsesMcpResourcesWithoutLeakingExtractedText()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var artifactId = Guid.NewGuid();
        await repository.AddArtifactAsync(new GradingArtifact(artifactId, item.Id, "submission_file", "entrega.pdf", "application/pdf", "aa", 12, "succeeded", "texto legado que nao deve vazar", null, DateTimeOffset.UtcNow, "https://moodle.example/pluginfile.php/1/entrega.pdf"), CancellationToken.None);
        var sut = new PrepareAiGradingBatchQueryHandler(repository, new RunCurrentUserContext("teacher-1"), new RunAssignmentSettingsGateway(), resourceGateway: new RunResourceGateway(), resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions { McpResourceSubmissionDeliveryEnabled = true }));

        var package = await sut.Handle(new PrepareAiGradingBatchQuery(batch.Id), CancellationToken.None);

        var result = Assert.Single(package.Items);
        Assert.Equal("mcp_resource", result.ResourceDeliveryMode);
        Assert.Null(result.ExtractedText);
        Assert.Equal("moodle://resource/0123456789abcdef0123456789abcdef", Assert.Single(result.Resources!).Uri);
        Assert.Equal("submission", Assert.Single(result.Resources!).ResourceType);
        Assert.Equal(artifactId, Assert.Single(result.Resources!).ArtifactId);
        Assert.Contains("resourceType", package.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAiBatch_NaoEscopaEnunciadoPorAluno()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var first = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var second = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        first.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        second.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(first, CancellationToken.None);
        await repository.AddItemAsync(second, CancellationToken.None);
        foreach (var item in new[] { first, second })
        {
            await repository.AddArtifactAsync(new GradingArtifact(
                Guid.NewGuid(), item.Id, "submission_file", "entrega.pdf", "application/pdf",
                null, 12, ExtractionStatus.Pending, null, "pending_resource", DateTimeOffset.UtcNow,
                $"https://moodle.example/pluginfile.php/{item.SubmissionId}/entrega.pdf"), CancellationToken.None);
            await repository.AddArtifactAsync(new GradingArtifact(
                Guid.NewGuid(), item.Id, "assignment_context", "enunciado.pdf", "application/pdf",
                null, 12, ExtractionStatus.Pending, null, "pending_resource", DateTimeOffset.UtcNow,
                "https://moodle.example/pluginfile.php/501/enunciado.pdf"), CancellationToken.None);
        }

        var resources = new RunResourceGateway();
        var sut = new PrepareAiGradingBatchQueryHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunAssignmentSettingsGateway(),
            resourceGateway: resources,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));

        var result = await sut.Handle(new PrepareAiGradingBatchQuery(batch.Id), CancellationToken.None);

        Assert.Equal(2, result.TotalItems);
        var contextRequests = resources.Requests
            .Where(request => request.ResourceType == "assignment_context_attachment")
            .ToArray();
        Assert.Single(contextRequests);
        Assert.All(contextRequests, request =>
        {
            Assert.Null(request.SubmissionId);
            Assert.Null(request.StudentId);
            Assert.Equal(501, request.AssignmentId);
        });
        var assignment = Assert.Single(result.Assignments!);
        Assert.Equal("501", assignment.AssignmentId);
        var context = Assert.Single(assignment.ContextResources);
        Assert.StartsWith("ctx-", context.ResourceId, StringComparison.Ordinal);
        Assert.All(result.Items, item =>
        {
            Assert.Single(item.Resources!);
            Assert.Equal("submission", Assert.Single(item.Resources!).ResourceType);
            Assert.Equal([context.ResourceId], item.ContextResourceRefs);
        });
        var submissionRequests = resources.Requests
            .Where(request => request.ResourceType == "submission_attachment")
            .ToArray();
        Assert.Equal(2, submissionRequests.Length);
        Assert.All(submissionRequests, request => Assert.NotNull(request.SubmissionId));
    }

    [Fact]
    public async Task PrepareAiBatch_ComMcpDesligado_NaoUsaTextoHistoricoNemFallback()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis("Pre-validacao concluida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(), item.Id, "submission_file", "entrega.rtf", "text/rtf", null, 4028,
                ExtractionStatus.Succeeded, "texto historico", null, DateTimeOffset.UtcNow,
                "https://moodle.example/pluginfile.php/1/entrega.rtf"),
            CancellationToken.None);

        var sut = new PrepareAiGradingBatchQueryHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunAssignmentSettingsGateway(),
            resourceGateway: new RunResourceGateway(),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = false,
                LegacySubmissionExtractionEnabled = false
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(new PrepareAiGradingBatchQuery(batch.Id), CancellationToken.None));
    }

    [Fact]
    public async Task PrepareSubmissionGrading_EntregaArquivoOriginalComoMcpResource()
    {
        var repository = new RunRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "submission_file",
                "resposta.rtf",
                "text/rtf",
                null,
                4028,
                ExtractionStatus.Failed,
                "texto local ignorado",
                "extract_failed",
                DateTimeOffset.UtcNow,
                "https://moodle.example/pluginfile.php/1/resposta.rtf"),
            CancellationToken.None);

        var sut = new PrepareGradingContextForChatQueryHandler(
            repository,
            new RunCurrentUserContext("teacher-1"),
            new RunAssignmentSettingsGateway(),
            resourceGateway: new RunResourceGateway(),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));

        var result = await sut.Handle(
            new PrepareGradingContextForChatQuery(item.Id),
            CancellationToken.None);

        var resource = Assert.Single(result.Resources!);
        Assert.Equal("resposta.rtf", resource.Name);
        Assert.Equal("text/rtf", resource.MimeType);
        Assert.Null(result.StudentSubmission);
        Assert.Contains(result.Warnings, warning => warning.Contains("MCP Resource", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("extracao falhou", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RunCurrentUserContext(string subject) : ICurrentUserContext
    {
        public string Subject { get; } = subject;
        public string? Email => "teacher@example.com";
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }

    private sealed class RunMoodleUserResolver : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken) => Task.FromResult<long?>(321);
    }

    private sealed class RunAuditLogRepository : IMoodleAuditLogRepository
    {
        public Task AddAsync(MoodleAuditLog log, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MoodleAuditLog>> ListByCorrelationIdAsync(string correlationId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);
        public Task<int> CountByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(Guid batchJobId, int page, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);
        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RunAssignmentSettingsGateway(decimal maxGrade = 10m) : IMoodleAssignmentSettingsGateway
    {
        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AssignmentSettingsSummary?>(new AssignmentSettingsSummary(assignmentId, maxGrade, "Atividade avaliativa"));
    }

    private sealed class RunResourceGateway : IMoodleResourceGateway
    {
        public List<MoodleResourceRegistration> Requests { get; } = [];

        public Task<MoodleResourceDescriptor> RegisterAsync(MoodleResourceRegistration request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new MoodleResourceDescriptor("moodle://resource/0123456789abcdef0123456789abcdef", request.Filename, request.MimeType, request.SizeBytes, request.Sha256));
        }
        public Task<MoodleResourceReadResult> ReadAsync(string uri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MoodleResourceDescriptor>> ExpandZipAsync(string uri, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodleResourceDescriptor>>([]);
    }

    private sealed class RunMoodleResourceRepository(IReadOnlyList<MoodleResource> resources) : IMoodleResourceRepository
    {
        public Task RegisterAsync(MoodleResource resource, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<MoodleResource?> FindAsync(string resourceId, CancellationToken cancellationToken) =>
            Task.FromResult(resources.SingleOrDefault(resource => resource.ResourceId == resourceId));
        public Task<IReadOnlyList<MoodleResource>> ListBySubmissionAsync(string clientId, string connectionId, long submissionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoodleResource>>(resources.Where(resource => resource.SubmissionId == submissionId).ToArray());
        public Task<bool> ExistsAndNotExpiredAsync(string resourceId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(resources.Any(resource => resource.ResourceId == resourceId && !resource.IsExpired(now)));
        public Task<int> RemoveExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RunSubmissionContentHashResolver(string hash) : ISubmissionContentHashResolver
    {
        public Task<SubmissionContentHashSnapshot> ResolveAsync(
            string userExternalId,
            string assignmentId,
            string studentId,
            long submissionId,
            IReadOnlyCollection<string> attachmentHashes,
            CancellationToken cancellationToken,
            AssignmentSubmissionRecord? currentSubmission = null) =>
            Task.FromResult(new SubmissionContentHashSnapshot(hash, 0, DateTimeOffset.UtcNow, attachmentHashes.Count));
    }

    private sealed class RunCourseContentsGateway(
        bool includeSecondAssignment = false,
        IReadOnlyList<string>? assignmentIds = null) : IMoodleCourseContentsGateway
    {
        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken)
        {
            if (courseId == "20")
            {
                throw new InvalidOperationException("Moodle indisponivel para o curso.");
            }

            var assignment = new CourseModuleSummary(
                "1001",
                "501",
                "assign",
                "Atividade avaliativa",
                null,
                Visible: true,
                UserVisible: true,
                Description: null,
                AvailabilityInfo: null,
                Dates: [],
                Files: []);
            CourseModuleSummary[] modules = assignmentIds is not null
                ? assignmentIds.Select((assignmentId, index) => new CourseModuleSummary(
                    (1001 + index).ToString(),
                    assignmentId,
                    "assign",
                    $"Atividade {assignmentId}",
                    null,
                    Visible: true,
                    UserVisible: true,
                    Description: null,
                    AvailabilityInfo: null,
                    Dates: [],
                    Files: [])).ToArray()
                : includeSecondAssignment
                ? new[]
                {
                    assignment,
                    new CourseModuleSummary(
                        "1002",
                        "502",
                        "assign",
                        "Outra atividade",
                        null,
                        Visible: true,
                        UserVisible: true,
                        Description: null,
                        AvailabilityInfo: null,
                        Dates: [],
                        Files: [])
                }
                : new[] { assignment };
            return Task.FromResult(new CourseContentsSummary(
                courseId,
                moduleTypes.ToArray(),
                includeHidden,
                onlyWithFiles,
                [new CourseSectionSummary("1", 1, "Topico", null, true, modules.Length, false, modules)]));
        }
    }

    private sealed class RunSubmissionGateway : IMoodleAssignmentSubmissionsGateway
    {
        public bool OmitBatchResult { get; init; }

        public IReadOnlyList<AssignmentSubmissionsBatch>? BatchResults { get; init; }

        public int BatchCalls { get; private set; }

        public string? LastStatus { get; private set; }

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            BatchCalls++;
            LastStatus = status;
            return Task.FromResult<IReadOnlyList<AssignmentSubmissionsBatch>>(BatchResults ?? (OmitBatchResult
                ? []
                : [
                new AssignmentSubmissionsBatch(
                    "501",
                    [
                        new AssignmentSubmissionRecord(
                            "submission-pending",
                            "9001",
                            "submitted",
                            "notgraded",
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            1,
                            1,
                            false,
                            []),
                        new AssignmentSubmissionRecord(
                            "submission-reviewed",
                            "9002",
                            "submitted",
                            "graded",
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            1,
                            1,
                            false,
                            [])
                    ])
                ]));
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

    private sealed class ThrowingCourseContentsGateway : IMoodleCourseContentsGateway
    {
        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("O fluxo snapshot-only não deveria ler conteúdos no Moodle.");
    }

    private sealed class RunSnapshotQueue : IMoodleSnapshotSyncQueue
    {
        public List<MoodleSnapshotSyncRequest> Requests { get; } = [];

        public bool Enqueue(MoodleSnapshotSyncRequest request)
        {
            Requests.Add(request);
            return true;
        }

        public Task<bool> EnqueueAsync(MoodleSnapshotSyncRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed class RunSnapshotStore(
        IReadOnlyList<CourseSummary> courses,
        MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot> submissions)
        : IMoodleSnapshotStore
    {
        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(
            Guid ownerId,
            string connectionAlias,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?>(
                new MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>(
                    courses,
                    DateTimeOffset.UtcNow,
                    IsStale: false,
                    IsFrozen: false,
                    Tier: "hot",
                    IsComplete: true,
                    RecordCount: courses.Count));

        public Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(
            Guid ownerId,
            string connectionAlias,
            string dataset,
            string courseId = "",
            CancellationToken cancellationToken = default)
        {
            if (dataset == MoodleSnapshotDatasets.Submissions && typeof(T) == typeof(CourseAssignmentSubmissionsSnapshot))
            {
                return Task.FromResult<MoodleSnapshotEnvelope<T>?>(
                    (MoodleSnapshotEnvelope<T>)(object)submissions);
            }

            return Task.FromResult<MoodleSnapshotEnvelope<T>?>(null);
        }

        public Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<CourseContentsSummary>?>(null);

        public Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<CourseParticipantsPage>?>(null);

        public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?>(null);

        public Task SaveAsync<T>(Guid ownerId, string connectionAlias, string dataset, string courseId, T payload, string tier, bool frozen, bool complete, int recordCount, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Invalidate(Guid ownerId, string connectionAlias, string dataset, string courseId = "") { }
    }

    private sealed class RunMediator(int pendingSubmissionCount = 2) : IMediator
    {
        public List<CreateAssistedGradingBatchCommand> CreateBatchRequests { get; } = [];
        public List<string> RequestedCourseIds { get; } = [];
        public List<string> RequestedAssignmentIds { get; } = [];
        public bool ReturnAlreadyQueued { get; init; }
        public ExistingGradingCorrectionReference? ExistingCorrection { get; init; }

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ListMyCoursesQuery)
            {
                return Task.FromResult((TResponse)(object)new PagedCourses(
                    [Course("10", "Curso valido"), Course("20", "Curso indisponivel")],
                    TotalCount: 2,
                    Page: 1,
                    PageSize: 10));
            }

            if (request is GetCourseQuery getCourse)
            {
                RequestedCourseIds.Add(getCourse.CourseId);
                CourseSummary? course = getCourse.CourseId == "10"
                    ? Course("10", "Curso valido")
                    : null;
                return Task.FromResult((TResponse)(object?)course!);
            }

            if (request is CreateAssistedGradingBatchCommand create)
            {
                CreateBatchRequests.Add(create);
                var itemCount = create.PrefetchedSubmissions?.Count ?? 0;
                var result = new CreateAssistedGradingBatchResult(
                    Guid.Parse($"00000000-0000-0000-0000-{100 + CreateBatchRequests.Count:D12}"),
                    create.CourseId,
                    create.AssignmentIds,
                    TotalItems: itemCount,
                    AcceptedItems: ReturnAlreadyQueued ? 0 : itemCount,
                    BlockedItems: 0,
                    Status: ReturnAlreadyQueued ? "AlreadyQueued" : "ReadyForReview",
                    Warnings: ReturnAlreadyQueued ? ["A entrega já pertence a uma correção anterior."] : [])
                {
                    ExistingCorrections = ReturnAlreadyQueued && ExistingCorrection is not null
                        ? [ExistingCorrection]
                        : []
                };
                return Task.FromResult((TResponse)(object)result);
            }

            if (request is ListAssignmentSubmissionsQuery submissions)
            {
                RequestedAssignmentIds.Add(submissions.AssignmentId);
                return Task.FromResult((TResponse)(object)new AssignmentSubmissionsPage(
                    submissions.CourseId,
                    submissions.AssignmentId,
                    AssignmentModuleId: "1001",
                    AssignmentName: "Atividade avaliativa",
                    submissions.Page,
                    submissions.PageSize,
                    submissions.Filter,
                    submissions.IncludeLate,
                    submissions.IncludeUngraded,
                    submissions.Since,
                    submissions.Before,
                    Total: pendingSubmissionCount,
                    HasMore: false,
                    Enumerable.Range(1, pendingSubmissionCount)
                        .Select(index => Submission((100 + index).ToString(), (9000 + index).ToString()))
                        .ToArray()));
            }

            throw new NotSupportedException($"Request nao suportado: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"Request nao suportado: {request.GetType().Name}");
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<TResponse>();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();

        private static CourseSummary Course(string id, string name) => new(
            id,
            IdNumber: null,
            ShortName: name,
            FullName: name,
            DisplayName: name,
            CategoryId: null,
            CategoryName: null,
            StartDate: null,
            EndDate: null,
            Visible: true,
            ViewUrl: null,
            CourseImage: null,
            Progress: null,
            HasProgress: null,
            IsFavourite: null,
            LastAccessAt: null);

        private static AssignmentSubmissionSummary Submission(string userId, string submissionId) => new(
            userId,
            FullName: null,
            SubmissionId: submissionId,
            Status: "submitted",
            GradingStatus: "notgraded",
            Submitted: true,
            Late: false,
            NeedsGrading: true,
            SubmittedAt: null,
            ModifiedAt: null,
            AttemptNumber: 0,
            FileCount: 1,
            HasOnlineText: false,
            Files: []);
    }

    private sealed class RunRepository : IGradingReviewRepository
    {
        private readonly List<AssistedGradingBatch> _batches = [];
        private readonly List<AssistedGradingItem> _items = [];
        private readonly List<GradingRun> _runs = [];
        private readonly List<GradingContextSnapshotDocument> _snapshots = [];
        private readonly List<GradingArtifact> _artifacts = [];

        public List<Guid> ListItemsByBatchCalls { get; } = [];

        public IReadOnlyList<GradingRun> Runs => _runs;

        public void AddRun(GradingRun run) => _runs.Add(run);

        public Task AddGradingRunAsync(GradingRun run, CancellationToken cancellationToken)
        {
            _runs.Add(run);
            return Task.CompletedTask;
        }

        public Task<GradingRunReservationResult> ReserveGradingRunAsync(
            GradingRun run,
            CancellationToken cancellationToken)
        {
            var existing = _runs.SingleOrDefault(candidate =>
                candidate.CreatedBySubject == run.CreatedBySubject &&
                candidate.ConnectorClientId == run.ConnectorClientId &&
                candidate.MoodleConnectionId == run.MoodleConnectionId &&
                candidate.IdempotencyKey == run.IdempotencyKey &&
                run.IdempotencyKey is not null);
            if (existing is not null)
            {
                return Task.FromResult(new GradingRunReservationResult(
                    existing,
                    Replayed: true,
                    PayloadConflict: !string.Equals(existing.RequestDigest, run.RequestDigest, StringComparison.Ordinal)));
            }

            _runs.Add(run);
            return Task.FromResult(new GradingRunReservationResult(run, Replayed: false, PayloadConflict: false));
        }

        public Task<GradingRun?> GetGradingRunAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_runs.SingleOrDefault(run => run.Id == id));

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByGradingRunAsync(
            Guid gradingRunId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(
                _batches.Where(batch => batch.GradingRunId == gradingRunId).ToArray());

        public void AddSnapshot(GradingContextSnapshot snapshot) =>
            _snapshots.Add(GradingContextSnapshotDocument.FromSnapshot(snapshot));

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken)
        {
            _batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_batches.SingleOrDefault(batch => batch.Id == id));

        public Task AddItemAsync(AssistedGradingItem item, CancellationToken cancellationToken)
        {
            _items.Add(item);
            return Task.CompletedTask;
        }

        public Task AddArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken) { _artifacts.Add(artifact); return Task.CompletedTask; }
        public Task AddEvidenceAsync(GradingEvidence evidence, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AssistedGradingItem?> GetItemAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_items.SingleOrDefault(item => item.Id == id));

        public Task<IReadOnlyList<GradingSubmissionMatch>> FindSubmissionMatchesAsync(
            long submissionId,
            long? courseId,
            long? assignmentId,
            string? moodleConnectionId,
            string? connectorClientId,
            string? connectionAlias,
            string? createdBySubject,
            CancellationToken cancellationToken)
        {
            var batchById = _batches.ToDictionary(batch => batch.Id);
            var matches = _items
                .Where(item => item.SubmissionId == submissionId &&
                               (!courseId.HasValue || item.CourseId == courseId) &&
                               (!assignmentId.HasValue || item.AssignmentId == assignmentId) &&
                               batchById.TryGetValue(item.BatchId, out var batch) &&
                               batch.Status != GradingBatchStatus.Cancelled &&
                               (string.IsNullOrWhiteSpace(createdBySubject) || batch.CreatedBySubject == createdBySubject))
                .Select(item =>
                {
                    var batch = batchById[item.BatchId];
                    return new GradingSubmissionMatch(
                        new GradingSubmissionIdentity(item.CourseId, item.AssignmentId, item.SubmissionId!.Value, item.AttemptNumber),
                        item.Id,
                        batch.Id,
                        batch.GradingRunId,
                        batch.CreatedBySubject,
                        item.Status,
                        item.CommitStatus,
                        batch.Status,
                        null);
                })
                .ToArray();
            return Task.FromResult<IReadOnlyList<GradingSubmissionMatch>>(matches);
        }

        public Task<IReadOnlyList<AssistedGradingItem>> ListItemsByBatchAsync(
            Guid batchId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            ListItemsByBatchCalls.Add(batchId);
            return Task.FromResult<IReadOnlyList<AssistedGradingItem>>(_items
                .Where(item => item.BatchId == batchId)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray());
        }

        public Task<int> CountItemsByBatchAsync(Guid batchId, CancellationToken cancellationToken) =>
            Task.FromResult(_items.Count(item => item.BatchId == batchId));
        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(Guid gradingItemId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GradingArtifact>>(_artifacts.Where(artifact => artifact.GradingItemId == gradingItemId).ToArray());
        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(Guid gradingItemId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GradingEvidence>>([]);
        public Task<IReadOnlyDictionary<Guid, GradingContextSnapshotDocument>> ListLatestContextSnapshotsByItemsAsync(
            IReadOnlyCollection<Guid> gradingItemIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, GradingContextSnapshotDocument>>(_snapshots
                .Where(snapshot => gradingItemIds.Contains(snapshot.GradingItemId))
                .GroupBy(snapshot => snapshot.GradingItemId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Version).First()));
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(GradingBatchStatus status, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssistedGradingBatch>>([]);
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssistedGradingBatch>>([]);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingProposalStore : IGradingProposalStore
    {
        public List<AiGradingProposal> Proposals { get; } = [];

        public Task<int> GetNextVersionAsync(Guid gradingItemId, CancellationToken cancellationToken) =>
            Task.FromResult(Proposals.Count(proposal => proposal.ItemId == gradingItemId) + 1);

        public Task PublishAsync(AiGradingProposal proposal, CancellationToken cancellationToken)
        {
            Proposals.Add(proposal);
            return Task.CompletedTask;
        }
    }
}
