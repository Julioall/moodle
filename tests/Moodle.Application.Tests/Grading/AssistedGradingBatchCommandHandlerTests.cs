using MediatR;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions;
using Moodle.Domain;
using Moodle.Domain.Grading;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moodle.Application.Tests.Grading;

public sealed class AssistedGradingBatchCommandHandlerTests
{
    [Fact]
    public async Task CreateBatch_CriaLoteComItensAguardandoCorrecao()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            new FakeCourseContentsGateway(),
            new FakeAssignmentSubmissionsGateway());

        var result = await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: [],
                MaxItems: 25,
                OnlyAwaitingGrading: true,
                IncludeRubric: false,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: true,
                TeacherInstructions: "Priorize clareza.",
                Priority: "high"),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.BatchJobId);
        Assert.Equal("10", result.CourseId);
        Assert.Equal(["501"], result.AssignmentIds);
        Assert.Equal(2, result.TotalItems);
        Assert.Equal(2, result.AcceptedItems);
        Assert.Equal(0, result.BlockedItems);
        Assert.Single(repository.Batches);
        Assert.Equal("Priorize clareza.", repository.Batches.Single().TeacherInstructions);
        Assert.Equal("high", repository.Batches.Single().Priority);
        Assert.False(repository.Batches.Single().IncludeRubric);
        Assert.False(repository.Batches.Single().IncludeSubmissionFiles);
        Assert.True(repository.Batches.Single().IncludeCourseMaterials);
        Assert.Contains(result.Warnings, warning => warning.Contains("includeSubmissionFiles=false", StringComparison.Ordinal));
        Assert.Equal(2, repository.Items.Count);
        Assert.All(repository.Items, item => Assert.Equal(GradingItemStatus.Pending, item.Status));
        Assert.All(repository.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.IdempotencyKey)));
        Assert.Null(mediator.LastListQuery);
        Assert.Equal(result.BatchJobId, orchestrator.LastEnqueuedBatchId);
    }

    [Fact]
    public async Task CreateBatch_OmitirIncludeSubmissionFilesMantemPadraoTrue()
    {
        var repository = new FakeGradingReviewRepository();
        var request = new CreateAssistedGradingBatchCommand(
            UserExternalId: "321",
            CourseId: "10",
            AssignmentIds: ["501"],
            SubmissionIds: [],
            MaxItems: 1,
            OnlyAwaitingGrading: true,
            IncludeRubric: false,
            PrefetchedSubmissions:
            [
                new AssignmentSubmissionSummary(
                    "101", "Synthetic Student", "9001", "submitted", "notgraded",
                    Submitted: true, Late: false, NeedsGrading: true,
                    SubmittedAt: null, ModifiedAt: null, AttemptNumber: 0,
                    FileCount: 0, HasOnlineText: true, Files: [])
            ]);
        var sut = CreateHandler(repository, new FakeMediator());

        var result = await sut.Handle(request, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.BatchJobId);
        Assert.True(Assert.Single(repository.Batches).IncludeSubmissionFiles);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("includeSubmissionFiles=false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateBatch_IgnoraEntregaJaPresenteEmOutroLote()
    {
        var repository = new FakeGradingReviewRepository();
        var existingBatch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var existingItem = AssistedGradingItem.Create(existingBatch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(existingBatch, CancellationToken.None);
        await repository.AddItemAsync(existingItem, CancellationToken.None);

        var sut = CreateHandler(repository, new FakeMediator());
        var result = await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                "321", "10", ["501"], ["9001"], 25, OnlyAwaitingGrading: true),
            CancellationToken.None);

        Assert.Equal(Guid.Empty, result.BatchJobId);
        Assert.Equal("AlreadyQueued", result.Status);
        Assert.Equal(0, result.AcceptedItems);
        Assert.Single(repository.Batches);
        Assert.Single(repository.Items);
        Assert.Contains(result.Warnings, warning => warning.Contains("evitadas", StringComparison.OrdinalIgnoreCase) || warning.Contains("duplicidade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateBatch_ReavaliacaoExplicitaAceitaEntregaJaPresente()
    {
        var repository = new FakeGradingReviewRepository();
        var existingBatch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var existingItem = AssistedGradingItem.Create(existingBatch.Id, 10, 501, 9001, 101, 0);
        existingItem.SetIdempotencyKey("correcao-original");
        await repository.AddBatchAsync(existingBatch, CancellationToken.None);
        await repository.AddItemAsync(existingItem, CancellationToken.None);

        var submissionsGateway = new FakeAssignmentSubmissionsGateway();
        var sut = CreateHandler(repository, new FakeMediator(), submissionsGateway);
        var result = await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                "321", "10", ["501"], ["9001"], 25,
                OnlyAwaitingGrading: true,
                IncludeAlreadyGraded: true),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.BatchJobId);
        Assert.Equal("Pending", result.Status);
        Assert.Equal(2, repository.Items.Count);
        Assert.Null(submissionsGateway.LastStatus);
        var regradedItem = Assert.Single(repository.Items, item => item.Id != existingItem.Id);
        Assert.Equal(9001, regradedItem.SubmissionId);
        Assert.NotEqual(existingItem.IdempotencyKey, regradedItem.IdempotencyKey);
    }

    [Fact]
    public async Task CreateBatch_NaoRepeteFluxoCompostoQuandoAListagemFalha()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var submissionsGateway = new FakeAssignmentSubmissionsGateway { ThrowOnRead = true };
        var orchestrator = new FakeGradingBatchOrchestrator();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            new FakeCourseContentsGateway(),
            submissionsGateway);

        await Assert.ThrowsAsync<MoodleApiException>(() => sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: [],
                MaxItems: 1,
                OnlyAwaitingGrading: true,
                IncludeRubric: false,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: false),
            CancellationToken.None));

        Assert.Equal(1, submissionsGateway.CallCount);
        Assert.Empty(repository.Batches);
    }

    [Fact]
    public async Task CreateBatch_AceitaIdDoModuloEResolveParaInstanciaDaTarefa()
    {
        var repository = new FakeGradingReviewRepository();
        var submissions = new FakeAssignmentSubmissionsGateway
        {
            NotFoundAssignmentIds = new HashSet<string>(["42"], StringComparer.Ordinal)
        };
        var contents = new FakeCourseContentsGateway();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            new FakeMediator(),
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeGradingBatchOrchestrator(),
            contents,
            submissions);

        var result = await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                "321", "10", ["42"], [], 1, OnlyAwaitingGrading: true,
                IncludeRubric: false, IncludeSubmissionFiles: false),
            CancellationToken.None);

        Assert.Equal(["501"], result.AssignmentIds);
        Assert.Equal(1, contents.CallCount);
        Assert.Equal(2, submissions.CallCount);
    }

    [Fact]
    public async Task CreateBatch_NaoConverteFalhaDeDescobertaEmNoPendingSubmissions()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var sut = CreateHandler(repository, mediator, new FakeAssignmentSubmissionsGateway
        {
            FailAssignmentIds = new HashSet<string>(["501"], StringComparer.Ordinal)
        });

        await Assert.ThrowsAsync<MoodleApiException>(() => sut.Handle(
            new CreateAssistedGradingBatchCommand(
                "321", "10", ["501"], [], 25, OnlyAwaitingGrading: true),
            CancellationToken.None));

        Assert.Empty(repository.Batches);
    }

    [Fact]
    public async Task CreateBatch_RetornaPartialFailureQuandoOutraAtividadeFalha()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var sut = CreateHandler(repository, mediator, new FakeAssignmentSubmissionsGateway
        {
            FailAssignmentIds = new HashSet<string>(["502"], StringComparer.Ordinal)
        });

        var result = await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                "321", "10", ["501", "502"], [], 25, OnlyAwaitingGrading: true),
            CancellationToken.None);

        Assert.Equal("PartialFailure", result.Status);
        var failure = Assert.Single(result.DiscoveryFailures!);
        Assert.Equal("502", failure.AssignmentId);
        Assert.Equal(MoodleErrorContract.NetworkError, failure.ErrorCode);
        Assert.NotEqual(Guid.Empty, result.BatchJobId);
    }

    [Fact]
    public async Task CreateBatch_ReutilizaLoteQuandoRequestIdERepetido()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var sut = CreateHandler(repository, mediator);
        var request = new CreateAssistedGradingBatchCommand(
            "321", "10", ["501"], [], 25, OnlyAwaitingGrading: true,
            IdempotencyKey: "mcp-request-504-1");

        var first = await sut.Handle(request, CancellationToken.None);
        var replay = await sut.Handle(request, CancellationToken.None);

        Assert.Equal(first.BatchJobId, replay.BatchJobId);
        Assert.Equal("IdempotentReplay", replay.Status);
        Assert.Single(repository.Batches);
        Assert.Equal(0, mediator.ListQueryCallCount);
    }

    [Fact]
    public async Task CreateBatch_PreservaConexaoResolvidaParaWorkerAssincrono()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var credentialsProvider = new FakeCredentialsProvider(
            new MoodleCredentials(
                "client-fieg",
                "connection-fieg",
                "fieg",
                "https://ead.fieg.com.br",
                "user",
                "password",
                "fieg",
                CanWrite: true));
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            new FakeCourseContentsGateway(),
            new FakeAssignmentSubmissionsGateway(),
            credentialsProvider: credentialsProvider);

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: [],
                MaxItems: 1,
                OnlyAwaitingGrading: true),
            CancellationToken.None);

        var batch = Assert.Single(repository.Batches);
        Assert.Equal("client-fieg", batch.ConnectorClientId);
        Assert.Equal("fieg", batch.ConnectionAlias);
        Assert.Equal(1, credentialsProvider.CallCount);
    }

    [Fact]
    public async Task CreateBatch_ComArquivosDeSubmissao_PersisteSomenteReferenciaOriginal()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            new FakeCourseContentsGateway(),
            new FakeAssignmentSubmissionsGateway());

        var result = await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9001"],
                MaxItems: 25,
                OnlyAwaitingGrading: true,
                IncludeSubmissionFiles: true),
            CancellationToken.None);

        var artifact = Assert.Single(repository.Artifacts, artifact => artifact.ArtifactType == "submission_file");
        Assert.Equal(repository.Items.Single().Id, artifact.GradingItemId);
        Assert.Equal("submission_file", artifact.ArtifactType);
        Assert.Equal("entrega.txt", artifact.Filename);
        Assert.Null(artifact.ExtractedTextRef);
        Assert.Equal("pending", artifact.ExtractionStatus);
        Assert.Equal("pending_resource", artifact.SummaryRef);
        Assert.Equal("https://moodle.example/pluginfile.php/entrega.txt", artifact.SourceUrl);
        Assert.Equal(result.BatchJobId, orchestrator.LastEnqueuedBatchId);
    }

    [Fact]
    public async Task CreateBatch_ComIngestaoDiferida_NaoBaixaExtraiNemConsultaConteudoPesado()
    {
        var repository = new FakeGradingReviewRepository();
        var contentsGateway = new FakeCourseContentsGateway();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            new FakeMediator(),
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeGradingBatchOrchestrator(),
            contentsGateway,
            new FakeAssignmentSubmissionsGateway(),
            Options.Create(new GradingLimitsOptions { DeferHeavyIngestion = true }));

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9001"],
                MaxItems: 25,
                OnlyAwaitingGrading: true,
                IncludeRubric: true,
                IncludeSubmissionFiles: true,
                IncludeCourseMaterials: true,
                PrefetchedSubmissions:
                [
                    new AssignmentSubmissionSummary(
                        "101",
                        "Ana Souza",
                        "9001",
                        "submitted",
                        "notgraded",
                        Submitted: true,
                        Late: false,
                        NeedsGrading: true,
                        SubmittedAt: null,
                        ModifiedAt: null,
                        AttemptNumber: 0,
                        FileCount: 1,
                        HasOnlineText: false,
                        Files:
                        [
                            new AssignmentSubmissionFile(
                                "entrega.txt",
                                "text/plain",
                                31,
                                "https://moodle.example/pluginfile.php/entrega.txt?token=nao-persistir")
                        ])
                ]),
            CancellationToken.None);

        Assert.Equal("Ana Souza", Assert.Single(repository.Items).StudentDisplayName);
        var artifact = Assert.Single(repository.Artifacts, item => item.ArtifactType == "submission_file");
        Assert.Equal(ExtractionStatus.Pending, artifact.ExtractionStatus);
        Assert.Equal("https://moodle.example/pluginfile.php/entrega.txt", artifact.SourceUrl);
        Assert.Equal("pending_resource", artifact.SummaryRef);
        Assert.Equal(0, contentsGateway.CallCount);
    }

    [Fact]
    public async Task CreateBatch_KeepsStudentNameUnavailableWhenPrefetchedScopeHasNoName()
    {
        var repository = new FakeGradingReviewRepository();
        var sut = CreateHandler(repository, new FakeMediator());

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9002"],
                MaxItems: 1,
                OnlyAwaitingGrading: true,
                IncludeSubmissionFiles: false,
                PrefetchedSubmissions:
                [
                    new AssignmentSubmissionSummary(
                        "102", null, "9002", "submitted", "notgraded",
                        Submitted: true, Late: false, NeedsGrading: true,
                        SubmittedAt: null, ModifiedAt: null, AttemptNumber: 0,
                        FileCount: 0, HasOnlineText: true)
                ]),
            CancellationToken.None);

        Assert.Null(Assert.Single(repository.Items).StudentDisplayName);
    }

    [Fact]
    public async Task CreateBatch_RefazLeituraSomenteQuandoListaDeArquivosEstaIncompleta()
    {
        async Task<(string[] SubmissionRefetches, GradingArtifact[] Artifacts, string[] Warnings)> CreateCase(
            int fileCount,
            IReadOnlyList<AssignmentSubmissionFile>? files,
            IReadOnlyList<AssignmentSubmissionRecord>? fallbackFiles = null)
        {
            var repository = new FakeGradingReviewRepository();
            var submissionsGateway = new FakeAssignmentSubmissionsGateway { IndividualResults = fallbackFiles };
            var sut = CreateHandler(repository, new FakeMediator(), submissionsGateway);

            var result = await sut.Handle(
                new CreateAssistedGradingBatchCommand(
                    UserExternalId: "321",
                    CourseId: "10",
                    AssignmentIds: ["501"],
                    SubmissionIds: ["9001"],
                    MaxItems: 1,
                    OnlyAwaitingGrading: true,
                    IncludeSubmissionFiles: true,
                    PrefetchedSubmissions:
                    [
                        new AssignmentSubmissionSummary(
                            "101", null, "9001", "submitted", "notgraded",
                            Submitted: true, Late: false, NeedsGrading: true,
                            SubmittedAt: null, ModifiedAt: null, AttemptNumber: 0,
                            FileCount: fileCount, HasOnlineText: false, Files: files)
                    ]),
                CancellationToken.None);

            return (
                submissionsGateway.IndividualAssignments.ToArray(),
                repository.Artifacts.Where(artifact => artifact.ArtifactType == "submission_file").ToArray(),
                result.Warnings.ToArray());
        }

        var completeEmptyCase = await CreateCase(0, []);
        var missingListCase = await CreateCase(1, null);
        var countMismatchCase = await CreateCase(2,
        [
            new AssignmentSubmissionFile(
                "partial.txt", "text/plain", 31, "https://moodle.example/pluginfile.php/partial.txt")
        ]);
        var emptyUrlCase = await CreateCase(1,
        [
            new AssignmentSubmissionFile("missing-url.txt", "text/plain", 31, " ")
        ]);
        var incompleteFallbackCase = await CreateCase(1, null, []);

        Assert.Empty(completeEmptyCase.SubmissionRefetches);
        Assert.Single(missingListCase.SubmissionRefetches);
        Assert.Single(countMismatchCase.SubmissionRefetches);
        Assert.Single(emptyUrlCase.SubmissionRefetches);
        Assert.Single(incompleteFallbackCase.SubmissionRefetches);
        Assert.Empty(completeEmptyCase.Artifacts);
        Assert.All(
            new[] { missingListCase, countMismatchCase, emptyUrlCase },
            testCase => Assert.Equal("https://moodle.example/pluginfile.php/entrega.txt", Assert.Single(testCase.Artifacts).SourceUrl));
        Assert.Empty(incompleteFallbackCase.Artifacts);
        Assert.Contains(incompleteFallbackCase.Warnings, warning => warning.Contains("incompleta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateBatch_ComMcpResourceAtivo_NaoDependeDeExtracaoMesmoSemIngestaoDiferida()
    {
        var repository = new FakeGradingReviewRepository();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            new FakeMediator(),
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeGradingBatchOrchestrator(),
            new FakeCourseContentsGateway(),
            new FakeAssignmentSubmissionsGateway(),
            Options.Create(new GradingLimitsOptions { DeferHeavyIngestion = false }));

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9001"],
                MaxItems: 25,
                OnlyAwaitingGrading: true,
                IncludeRubric: false,
                IncludeSubmissionFiles: true,
                PrefetchedSubmissions:
                [
                    new AssignmentSubmissionSummary(
                        "101",
                        "Ana Souza",
                        "9001",
                        "submitted",
                        "notgraded",
                        Submitted: true,
                        Late: false,
                        NeedsGrading: true,
                        SubmittedAt: null,
                        ModifiedAt: null,
                        AttemptNumber: 0,
                        FileCount: 1,
                        HasOnlineText: false,
                        Files:
                        [
                            new AssignmentSubmissionFile(
                                "resposta.rtf",
                                "text/rtf",
                                4028,
                                "https://moodle.example/pluginfile.php/resposta.rtf")
                        ])
                ]),
            CancellationToken.None);

        var artifact = Assert.Single(repository.Artifacts, item => item.ArtifactType == "submission_file");
        Assert.Equal(ExtractionStatus.Pending, artifact.ExtractionStatus);
        Assert.Equal("text/rtf", artifact.MimeType);
        Assert.Equal("resposta.rtf", artifact.Filename);
        Assert.Equal("https://moodle.example/pluginfile.php/resposta.rtf", artifact.SourceUrl);
    }

    [Fact]
    public async Task IngestionService_NaoBaixaNemExtraiArquivos()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            includeRubric: false,
            includeSubmissionFiles: true,
            includeCourseMaterials: false);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(), item.Id, "submission_file", "entrega.txt", "text/plain",
                Sha256: null, SizeBytes: 31, ExtractionStatus.Pending, ExtractedTextRef: null,
                SummaryRef: "pending_resource", DateTimeOffset.UtcNow,
                "https://moodle.example/pluginfile.php/entrega.txt"),
            CancellationToken.None);

        var sut = new GradingArtifactIngestionService(
            repository,
            new FakeAssignmentSubmissionsGateway(),
            new FakeCourseContentsGateway(),
            Options.Create(new GradingLimitsOptions()),
            NullLogger<GradingArtifactIngestionService>.Instance);

        await sut.IngestPendingAsync(batch, item, CancellationToken.None);

        var deferred = Assert.Single(repository.Artifacts);
        Assert.Equal(ExtractionStatus.Pending, deferred.ExtractionStatus);
        Assert.Null(deferred.ExtractedTextRef);
        Assert.NotNull(deferred.SourceUrl);
    }

    [Fact]
    public async Task IngestionService_RetentaLeituraDeContextoTransitóriaAntesDeCriarArtifacts()
    {
        var repository = new FakeGradingReviewRepository();
        var contentsGateway = new FakeCourseContentsGateway { FailuresBeforeSuccess = 2 };
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            includeRubric: false,
            includeSubmissionFiles: false,
            includeCourseMaterials: true);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);

        var sut = new GradingArtifactIngestionService(
            repository,
            new FakeAssignmentSubmissionsGateway(),
            contentsGateway,
            Options.Create(new GradingLimitsOptions()),
            NullLogger<GradingArtifactIngestionService>.Instance);

        await sut.IngestPendingAsync(batch, item, CancellationToken.None);

        Assert.Equal(3, contentsGateway.CallCount);
        Assert.Contains(repository.Artifacts, artifact => artifact.ArtifactType == "assignment_context");
        Assert.DoesNotContain(repository.Artifacts, artifact => artifact.SummaryRef == "context_fetch_failed");
    }

    [Fact]
    public async Task IngestionService_PersisteDiagnosticoQuandoContextoNaoPodeSerLido()
    {
        var repository = new FakeGradingReviewRepository();
        var contentsGateway = new FakeCourseContentsGateway { FailuresBeforeSuccess = 3 };
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            includeRubric: false,
            includeSubmissionFiles: false,
            includeCourseMaterials: true);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);

        var sut = new GradingArtifactIngestionService(
            repository,
            new FakeAssignmentSubmissionsGateway(),
            contentsGateway,
            Options.Create(new GradingLimitsOptions()),
            NullLogger<GradingArtifactIngestionService>.Instance);

        await sut.IngestPendingAsync(batch, item, CancellationToken.None);

        var diagnostic = Assert.Single(repository.Artifacts, artifact => artifact.ArtifactType == "assignment_context");
        Assert.Equal("context_fetch_failed", diagnostic.SummaryRef);
        Assert.Equal(ExtractionStatus.Failed, diagnostic.ExtractionStatus);
    }

    [Fact]
    public async Task IngestionService_PreservaAnexoOficialDoAssignMesmoSemCourseContents()
    {
        var repository = new FakeGradingReviewRepository();
        var contentsGateway = new FakeCourseContentsGateway { FailuresBeforeSuccess = 3 };
        var settingsGateway = new FakeMoodleAssignmentSettingsGateway
        {
            Settings = new AssignmentSettingsSummary(
                "9001",
                10m,
                "Atividade oficial",
                IsGradable: true,
                Description: "Enunciado oficial",
                RawGrade: 10m,
                IntroAttachments:
                [
                    new CourseModuleFile(
                        "file",
                        "enunciado.pdf",
                        "/",
                        128,
                        "application/pdf",
                        "https://moodle.example/webservice/pluginfile.php/1/mod_assign/introattachment/0/enunciado.pdf?token=synthetic",
                        false)
                ],
                IntroAttachmentsComplete: true)
        };
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            includeRubric: true,
            includeSubmissionFiles: false,
            includeCourseMaterials: false);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);

        var sut = new GradingArtifactIngestionService(
            repository,
            new FakeAssignmentSubmissionsGateway(),
            contentsGateway,
            Options.Create(new GradingLimitsOptions()),
            NullLogger<GradingArtifactIngestionService>.Instance,
            settingsGateway: settingsGateway);

        await sut.IngestPendingAsync(batch, item, CancellationToken.None);

        var intro = Assert.Single(repository.Artifacts, artifact => artifact.SummaryRef == "official_assignment_intro");
        Assert.Equal("Enunciado oficial", intro.ExtractedTextRef);
        var attachment = Assert.Single(repository.Artifacts, artifact => artifact.SummaryRef == "official_intro_attachment");
        Assert.Equal(ExtractionStatus.Pending, attachment.ExtractionStatus);
        Assert.DoesNotContain("token=", attachment.SourceUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, contentsGateway.CallCount);
    }

    [Fact]
    public async Task IngestionService_PreservaEnunciadoEAnexosOficiaisQuandoCourseContentsNaoTemDescricaoNemArquivosEDeduplica()
    {
        var repository = new FakeGradingReviewRepository();
        var contentsGateway = new FakeCourseContentsGateway { ReturnEmptyContents = true };
        const string pdfUrl = "https://moodle.example/pluginfile.php/11/mod_assign/introattachment/0/enunciado.pdf?token=synthetic";
        var settingsGateway = new FakeMoodleAssignmentSettingsGateway
        {
            Settings = new AssignmentSettingsSummary(
                "501",
                40m,
                "Atividade oficial",
                IsGradable: true,
                Description: "<p>Enunciado oficial em HTML.</p>",
                RawGrade: 40m,
                IntroAttachments:
                [
                    new CourseModuleFile("file", "enunciado.pdf", "/", 128, "application/pdf", pdfUrl, false),
                    new CourseModuleFile("file", "enunciado-copia.pdf", "/", 128, "application/pdf", pdfUrl, false),
                    new CourseModuleFile("file", "modelo.docx", "/", 256, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "https://moodle.example/pluginfile.php/11/mod_assign/introattachment/0/modelo.docx?token=synthetic", false)
                ],
                IntroAttachmentsComplete: true)
        };
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            includeRubric: true,
            includeSubmissionFiles: false,
            includeCourseMaterials: false);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var sut = new GradingArtifactIngestionService(
            repository,
            new FakeAssignmentSubmissionsGateway(),
            contentsGateway,
            Options.Create(new GradingLimitsOptions()),
            NullLogger<GradingArtifactIngestionService>.Instance,
            settingsGateway: settingsGateway);

        await sut.IngestPendingAsync(batch, item, CancellationToken.None);

        Assert.Equal(1, contentsGateway.CallCount);
        Assert.Equal("<p>Enunciado oficial em HTML.</p>", Assert.Single(repository.Artifacts, artifact => artifact.SummaryRef == "official_assignment_intro").ExtractedTextRef);
        var attachments = repository.Artifacts.Where(artifact => artifact.SummaryRef == "official_intro_attachment").ToArray();
        Assert.Equal(2, attachments.Length);
        Assert.Contains(attachments, artifact => artifact.Filename == "enunciado.pdf");
        Assert.Contains(attachments, artifact => artifact.Filename == "modelo.docx");
        Assert.All(attachments, artifact => Assert.DoesNotContain("token=", artifact.SourceUrl, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(repository.Artifacts, artifact => artifact.SummaryRef == "context_assignment_not_found");
    }

    [Fact]
    public async Task IngestionService_NaoConfundeFalhaDeLeituraDeArquivosComEntregaSemAnexo()
    {
        var repository = new FakeGradingReviewRepository();
        var submissionsGateway = new FakeAssignmentSubmissionsGateway { ThrowOnIndividualRead = true };
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            includeRubric: false,
            includeSubmissionFiles: true,
            includeCourseMaterials: false);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);

        var sut = new GradingArtifactIngestionService(
            repository,
            submissionsGateway,
            new FakeCourseContentsGateway(),
            Options.Create(new GradingLimitsOptions()),
            NullLogger<GradingArtifactIngestionService>.Instance);

        var exception = await Assert.ThrowsAsync<GradingTransientDependencyException>(() =>
            sut.IngestPendingAsync(batch, item, CancellationToken.None));

        Assert.Contains("arquivos", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repository.Artifacts);
        Assert.Equal(3, submissionsGateway.IndividualCallCount);
    }

    [Fact]
    public async Task CreateBatch_ComMateriaisDoCurso_SalvaArtefatoDeContextoDaTarefa()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            new FakeCourseContentsGateway(),
            new FakeAssignmentSubmissionsGateway());

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9001"],
                MaxItems: 25,
                OnlyAwaitingGrading: true,
                IncludeRubric: true,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: true),
            CancellationToken.None);

        var contextArtifacts = repository.Artifacts
            .Where(artifact => artifact.ArtifactType == "assignment_context")
            .ToArray();
        Assert.Contains(contextArtifacts, artifact =>
            artifact.Filename == "Orientacoes SAP 01 - Etapa 1.pdf" &&
            artifact.ExtractedTextRef is null &&
            artifact.ExtractionStatus == ExtractionStatus.Pending &&
            artifact.SourceUrl == "https://moodle.example/pluginfile.php/orientacoes.pdf");
        Assert.Contains(contextArtifacts, artifact =>
            artifact.Filename == "Tarefa 1" &&
            artifact.ExtractedTextRef!.Contains("Descricao da tarefa SAP 01", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateBatch_QuandoContextoFalha_PersisteDiagnosticoParaWorkerTentarNovamente()
    {
        var repository = new FakeGradingReviewRepository();
        var contentsGateway = new FakeCourseContentsGateway { FailuresBeforeSuccess = 3 };
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            new FakeMediator(),
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeGradingBatchOrchestrator(),
            contentsGateway,
            new FakeAssignmentSubmissionsGateway());

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9001"],
                MaxItems: 1,
                OnlyAwaitingGrading: true,
                IncludeRubric: false,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.Equal(3, contentsGateway.CallCount);
        var diagnostic = Assert.Single(repository.Artifacts, artifact => artifact.ArtifactType == "assignment_context");
        Assert.Equal(ExtractionStatus.Failed, diagnostic.ExtractionStatus);
        Assert.Equal("context_fetch_failed", diagnostic.SummaryRef);
    }

    [Fact]
    public async Task CreateBatch_SemRubrica_ComMateriais_NaoPersisteDescricaoDaAtividadeComoRubrica()
    {
        var repository = new FakeGradingReviewRepository();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            new FakeMediator(),
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeGradingBatchOrchestrator(),
            new FakeCourseContentsGateway(),
            new FakeAssignmentSubmissionsGateway());

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                "321", "10", ["501"], ["9001"], 25, true,
                IncludeRubric: false,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.DoesNotContain(repository.Artifacts, artifact => artifact.SummaryRef == "assignment_description");
        Assert.Contains(repository.Artifacts, artifact => artifact.Filename == "Orientacoes SAP 01 - Etapa 1.pdf");
    }

    [Fact]
    public async Task CreateBatch_SemMateriaisDoCurso_IncluiEnunciadoCorrespondenteMasNaoMaterialGenerico()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var contentsGateway = new FakeCourseContentsGateway();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            contentsGateway,
            new FakeAssignmentSubmissionsGateway());

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9001"],
                MaxItems: 25,
                OnlyAwaitingGrading: true,
                IncludeRubric: true,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: false),
            CancellationToken.None);

        Assert.Equal(1, contentsGateway.CallCount);
        Assert.Contains(repository.Artifacts, artifact => artifact.Filename == "Tarefa 1");
        Assert.Contains(repository.Artifacts, artifact => artifact.Filename == "Orientacoes SAP 01 - Etapa 1.pdf");
        Assert.DoesNotContain(repository.Artifacts, artifact => artifact.Filename == "Calendario do curso");
    }

    [Fact]
    public async Task CreateBatch_ComDoisItensDaMesmaTarefa_ReusaCacheDeContexto()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var contentsGateway = new FakeCourseContentsGateway();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            contentsGateway,
            new FakeAssignmentSubmissionsGateway());

        await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: [],
                MaxItems: 25,
                OnlyAwaitingGrading: true,
                IncludeRubric: true,
                IncludeSubmissionFiles: false,
                IncludeCourseMaterials: true),
            CancellationToken.None);

        Assert.Equal(2, repository.Items.Count);
        Assert.Equal(1, contentsGateway.CallCount);

        var contextArtifacts = repository.Artifacts
            .Where(artifact => artifact.ArtifactType == "assignment_context")
            .ToArray();
        Assert.Equal(4, contextArtifacts.Length);
        foreach (var item in repository.Items)
        {
            Assert.Contains(contextArtifacts, artifact =>
                artifact.GradingItemId == item.Id &&
                artifact.Filename == "Tarefa 1");
            Assert.Contains(contextArtifacts, artifact =>
                artifact.GradingItemId == item.Id &&
                artifact.Filename == "Orientacoes SAP 01 - Etapa 1.pdf");
            Assert.DoesNotContain(contextArtifacts, artifact =>
                artifact.GradingItemId == item.Id &&
                artifact.Filename == "Calendario do curso");
        }
    }

    [Fact]
    public async Task CreateBatch_RespeitaMaxItemsESubmissionIds()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            new FakeCourseContentsGateway(),
            new FakeAssignmentSubmissionsGateway());

        var result = await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9002"],
                MaxItems: 1,
                OnlyAwaitingGrading: true),
            CancellationToken.None);

        Assert.Equal(1, result.AcceptedItems);
        var item = Assert.Single(repository.Items);
        Assert.Equal(9002, item.SubmissionId);
        Assert.Equal(102, item.MoodleUserId);
        Assert.Null(mediator.LastListQuery);
        Assert.Equal(result.BatchJobId, orchestrator.LastEnqueuedBatchId);
    }

    [Fact]
    public async Task CreateBatch_SelecionaSubmissoesSemRefazerPaginacaoDoAgregador()
    {
        var repository = new FakeGradingReviewRepository();
        var mediator = new FakeMediator();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var sut = new CreateAssistedGradingBatchCommandHandler(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            orchestrator,
            new FakeCourseContentsGateway(),
            new FakeAssignmentSubmissionsGateway());

        var result = await sut.Handle(
            new CreateAssistedGradingBatchCommand(
                UserExternalId: "321",
                CourseId: "10",
                AssignmentIds: ["501"],
                SubmissionIds: ["9001", "9002"],
                MaxItems: 2,
                OnlyAwaitingGrading: true),
            CancellationToken.None);

        Assert.Equal(2, result.AcceptedItems);
        Assert.Equal([9001L, 9002L], repository.Items.Select(item => item.SubmissionId).ToArray());
        Assert.Equal(0, mediator.ListQueryCallCount);
    }

    [Fact]
    public async Task CancelBatch_ChamaOrquestradorERetornaStatusCancelado()
    {
        var repository = new FakeGradingReviewRepository();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        orchestrator.BatchLookup = id => repository.Batches.SingleOrDefault(candidate => candidate.Id == id);
        var sut = new CancelAssistedGradingBatchCommandHandler(
            orchestrator,
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new CancelAssistedGradingBatchCommand(batch.Id),
            CancellationToken.None);

        Assert.Equal(batch.Id, orchestrator.LastCancelledBatchId);
        Assert.Equal(batch.Id, result.BatchJobId);
        Assert.Equal("Cancelled", result.Status);
        Assert.Contains("cancelado", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelBatch_SemExpurgoNaoDependeDaLeituraDosItens()
    {
        var repository = new FakeGradingReviewRepository { FailListItemsByBatch = true };
        var orchestrator = new FakeGradingBatchOrchestrator();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        orchestrator.BatchLookup = id => repository.Batches.SingleOrDefault(candidate => candidate.Id == id);
        var sut = new CancelAssistedGradingBatchCommandHandler(
            orchestrator,
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new CancelAssistedGradingBatchCommand(batch.Id),
            CancellationToken.None);

        Assert.Equal(batch.Id, orchestrator.LastCancelledBatchId);
        Assert.Equal("Cancelled", result.Status);
        Assert.Equal(GradingBatchStatus.Cancelled, batch.Status);
        Assert.Empty(result.Warnings ?? []);
    }

    [Fact]
    public async Task CancelBatch_ConcluidoSemPublicacaoPodeSerCanceladoParaDestravarDuplicidade()
    {
        var repository = new FakeGradingReviewRepository();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho salvo.");
        batch.MarkCompleted();
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new CancelAssistedGradingBatchCommandHandler(
            orchestrator,
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new CancelAssistedGradingBatchCommand(batch.Id),
            CancellationToken.None);

        Assert.Equal("Cancelled", result.Status);
        Assert.Equal(GradingBatchStatus.Cancelled, batch.Status);
        Assert.Null(orchestrator.LastCancelledBatchId);
    }

    [Fact]
    public async Task CancelBatch_ComPublicacaoEfetivadaCancelaEMantemHistorico()
    {
        var repository = new FakeGradingReviewRepository();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkCommitSucceeded();
        batch.MarkCompleted();
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new CancelAssistedGradingBatchCommandHandler(
            orchestrator,
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new CancelAssistedGradingBatchCommand(batch.Id),
            CancellationToken.None);

        Assert.Equal(GradingBatchStatus.Cancelled, batch.Status);
        Assert.Null(orchestrator.LastCancelledBatchId);
        Assert.Equal(1, result.PublishedItemsPreserved);
        Assert.Contains(result.Warnings!, warning => warning.Contains("publicado", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancelBatch_ComFalhaNaLeituraPreservaExpurgoMasCancelaExecucao()
    {
        var repository = new FakeGradingReviewRepository { FailListItemsByBatch = true };
        var orchestrator = new FakeGradingBatchOrchestrator();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        orchestrator.BatchLookup = id => repository.Batches.SingleOrDefault(candidate => candidate.Id == id);
        var sut = new CancelAssistedGradingBatchCommandHandler(
            orchestrator,
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new CancelAssistedGradingBatchCommand(
                batch.Id,
                PurgeLocalData: true,
                ConfirmationText: "CANCELAR_CORRECOES_LOCAIS"),
            CancellationToken.None);

        Assert.Equal("Cancelled", result.Status);
        Assert.False(result.Purged);
        Assert.Equal(GradingBatchStatus.Cancelled, batch.Status);
        Assert.Contains(result.Warnings ?? [], warning => warning.Contains("leitura do resumo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancelBatch_DeOutroCriadorSemEscopoAdmin_DeveFalhar()
    {
        var repository = new FakeGradingReviewRepository();
        var orchestrator = new FakeGradingBatchOrchestrator();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = new CancelAssistedGradingBatchCommandHandler(
            orchestrator,
            repository,
            new FakeCurrentUserContext("teacher-2"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sut.Handle(new CancelAssistedGradingBatchCommand(batch.Id), CancellationToken.None));

        Assert.Null(orchestrator.LastCancelledBatchId);
    }

    [Fact]
    public async Task GetBatchStatus_RetornaResumoPaginado()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Snapshots.Add(Snapshot(item));
        await repository.SaveChangesAsync(CancellationToken.None);
        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);

        Assert.Equal(batch.Id, result.BatchJobId);
        Assert.Equal("Pending", result.Status);
        Assert.Equal(1, result.TotalItems);
        var statusItem = Assert.Single(result.Items);
        Assert.Equal(item.Id, statusItem.GradingItemId);
        Assert.Equal("DraftReady", statusItem.Status);
        Assert.False(result.HasMore);
        Assert.Equal(0, result.ProcessingMetrics.ProgressPercent);
        Assert.False(result.ProcessingMetrics.CanLaunch);
        Assert.Single(result.NextReadyItems);
        Assert.Empty(result.ErrorsByCategory);
        Assert.Equal("create_batch_grade_launch_preview", result.SuggestedAction);
        Assert.Null(result.SuggestedActionLimitation);
    }

    [Fact]
    public async Task GetBatchStatus_ExposesCommitFailureReason()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8m, "Feedback revisado.", "teacher-1", 321, "approved", null);
        item.MarkCommitFailed("moodle_timeout");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Snapshots.Add(Snapshot(item));
        await repository.SaveChangesAsync(CancellationToken.None);
        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);

        Assert.Equal("publication_failed", Assert.Single(result.Items).FailureReason);
    }

    [Fact]
    public async Task GetBatchStatus_ProjectsAuthorizedPublicationAndReturnsActionReference()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8m, "Feedback revisado.", "teacher-1", 321, "approved", null);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Snapshots.Add(Snapshot(item));
        await repository.SaveChangesAsync(CancellationToken.None);

        var pendingRepository = new FakePublicationStatusRepository();
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new GradingLaunchPayload(
                    batch.Id,
                    [new GradingLaunchPayloadItem(item.Id, "10", "501", "101", 8m, "Feedback revisado.", 0, "draft-hash")]),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        };
        pendingRepository.Actions.Add(action);

        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"),
            pendingRepository);
        var awaitingApproval = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);
        Assert.Equal("confirm_batch_grade_launch", awaitingApproval.SuggestedAction);

        action.Authorize("teacher-1", DateTimeOffset.UtcNow);
        var result = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);

        Assert.Equal(action.Id, Assert.Single(result.PublicationActions!).PendingActionId);
        Assert.Equal("queued", Assert.Single(result.Items).PublicationState);
        Assert.Equal("get_grading_publication_status", result.SuggestedAction);
        Assert.Equal(2, pendingRepository.BulkListCallCount);
        Assert.NotNull(result.AsOfUtc);

        action.MarkExecuted();
        var inconsistent = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);
        var inconsistentItem = Assert.Single(inconsistent.Items);
        Assert.Equal("unknown", inconsistentItem.PublicationState);
        Assert.Equal("publication_item_state_inconsistent", inconsistentItem.FailureReason);
    }

    [Fact]
    public async Task GetBatchStatus_LeavesUntouchedItemsReadyWhenAnotherWriteIsUnknown()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501, 502], "teacher-1", 321, totalItems: 2);
        var attemptedItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        attemptedItem.SetDraft(8m, 0.8m, "Rascunho tentado.");
        attemptedItem.ApplyTeacherReview(8m, "Feedback tentado.", "teacher-1", 321, "approved", null);
        attemptedItem.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow.AddMinutes(-1));
        attemptedItem.MarkCommitExecutionUnknown("Resposta perdida.");

        var untouchedItem = AssistedGradingItem.Create(batch.Id, 10, 502, 9002, 102, 0);
        untouchedItem.SetDraft(7m, 0.7m, "Rascunho ainda não enviado.");
        untouchedItem.ApplyTeacherReview(7m, "Feedback ainda não enviado.", "teacher-1", 321, "approved", null);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(attemptedItem, CancellationToken.None);
        await repository.AddItemAsync(untouchedItem, CancellationToken.None);
        repository.Snapshots.Add(Snapshot(attemptedItem));
        repository.Snapshots.Add(Snapshot(untouchedItem));
        await repository.SaveChangesAsync(CancellationToken.None);

        var pendingRepository = new FakePublicationStatusRepository();
        var action = new PendingMoodleAction
        {
            ToolName = "criar_previa_lancamento_lote",
            CreatedBySubject = "teacher-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new GradingLaunchPayload(
                    batch.Id,
                    [
                        new GradingLaunchPayloadItem(attemptedItem.Id, "10", "501", "101", 8m, "Feedback tentado.", 0, "draft-hash-1"),
                        new GradingLaunchPayloadItem(untouchedItem.Id, "10", "502", "102", 7m, "Feedback ainda não enviado.", 0, "draft-hash-2")
                    ]),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        };
        action.Authorize("teacher-1", DateTimeOffset.UtcNow);
        action.MarkExecutionUnknown("Resposta perdida.");
        pendingRepository.Actions.Add(action);

        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"),
            pendingRepository);
        var result = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);

        var attemptedResult = Assert.Single(result.Items, item => item.GradingItemId == attemptedItem.Id);
        var untouchedResult = Assert.Single(result.Items, item => item.GradingItemId == untouchedItem.Id);
        Assert.Equal("unknown", attemptedResult.PublicationState);
        Assert.Equal("ready", untouchedResult.PublicationState);
        Assert.Equal("moodle_reconcile_write", result.SuggestedAction);
        Assert.Contains("resultado remoto desconhecido", result.SuggestedActionLimitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBatchStatus_PublishedItemsRecommendNoFurtherAction()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkCommitSucceeded();
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);

        Assert.Equal("none", result.SuggestedAction);
        Assert.Null(result.SuggestedActionLimitation);
    }

    [Fact]
    public async Task GetBatchStatus_PendingItemsRecommendPollingStatus()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);

        Assert.Equal("get_assisted_grading_batch_status", result.SuggestedAction);
        Assert.Null(result.SuggestedActionLimitation);
    }

    [Fact]
    public async Task GetBatchStatus_FailedLegacyStateHasNoAutomaticRecommendation()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAnalysisFailed("Falha simulada.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);

        Assert.Null(result.SuggestedAction);
        Assert.Contains("sem ação automática segura", result.SuggestedActionLimitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBatchStatus_UnknownPublicationWithoutVisibleActionDoesNotRecommendUnauthorizedReconciliation()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.TryMarkCommitWriteIntent(DateTimeOffset.UtcNow.AddMinutes(-1));
        item.MarkCommitExecutionUnknown("Resposta perdida.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10),
            CancellationToken.None);

        Assert.Null(result.SuggestedAction);
        Assert.Contains("nenhuma ação reconciliável está visível", result.SuggestedActionLimitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCoordinationReport_ConsolidaLoteInteiroParaCoordenacao()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501, 502], "teacher-1", 321, totalItems: 3);
        var lowConfidenceItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var reviewedItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        var failedItem = AssistedGradingItem.Create(batch.Id, 10, 502, 9003, 103, 0);
        lowConfidenceItem.SetDraft(4m, 0.35m, "Rascunho de baixa confianca.", "Revisar criterios.");
        reviewedItem.SetDraft(9m, 0.8m, "Rascunho consistente.");
        reviewedItem.ApplyTeacherReview(9.5m, "Feedback final revisado.", "teacher-1", 321, "approved");
        failedItem.MarkAnalysisFailed("Falha simulada ao processar contexto.");
        batch.UpdateCounters(processedItems: 3, readyItems: 2, blockedItems: 0, failedItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(lowConfidenceItem, CancellationToken.None);
        await repository.AddItemAsync(reviewedItem, CancellationToken.None);
        await repository.AddItemAsync(failedItem, CancellationToken.None);
        await repository.AddEvidenceAsync(
            new GradingEvidence(
                Guid.NewGuid(),
                lowConfidenceItem.Id,
                "c1",
                "Descrever eventos de TI.",
                4m,
                2m,
                "Evidencia parcial.",
                "Faltou exemplo operacional.",
                TeacherReviewRequired: true,
                CreatedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);
        await repository.AddEvidenceAsync(
            new GradingEvidence(
                Guid.NewGuid(),
                reviewedItem.Id,
                "c1",
                "Descrever eventos de TI.",
                4m,
                4m,
                "Evidencia suficiente.",
                GapsText: null,
                TeacherReviewRequired: false,
                CreatedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);
        var sut = new GetAssistedGradingCoordinationReportQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingCoordinationReportQuery(batch.Id),
            CancellationToken.None);

        Assert.Equal(batch.Id, result.BatchJobId);
        Assert.Equal("10", result.CourseId);
        Assert.Equal(["501", "502"], result.AssignmentIds);
        Assert.Equal("ReadyForReview", result.Status);
        Assert.Equal(3, result.TotalItems);
        Assert.Equal(1, result.ReviewedItems);
        Assert.Equal(1, result.PendingReviewItems);
        Assert.Equal(1, result.LaunchPendingItems);
        Assert.Equal(1, result.LowConfidenceItems);
        Assert.Equal(1, result.FailedItems);
        Assert.Equal(0.38m, result.AverageConfidence);
        Assert.Equal(6.5m, result.AverageSuggestedGrade);
        Assert.Equal(9.5m, result.AverageFinalGrade);
        Assert.Equal(1, result.StatusCounts["Failed"]);
        Assert.Equal(1, result.ReviewStatusCounts["Reviewed"]);
        Assert.Equal(1, result.CommitStatusCounts["Pending"]);
        Assert.Contains(result.AttentionItems, item =>
            item.GradingItemId == lowConfidenceItem.Id &&
            item.Reason.Contains("Baixa confianca", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.AttentionItems, item =>
            item.GradingItemId == failedItem.Id &&
            item.Reason.Contains("Falha no processamento", StringComparison.OrdinalIgnoreCase));
        var criterion = Assert.Single(result.CriteriaNeedingReview);
        Assert.Equal("c1", criterion.CriterionId);
        Assert.Equal(2, criterion.ItemCount);
        Assert.Equal(1, criterion.TeacherReviewRequiredItems);
        Assert.Equal(1, criterion.ItemsWithGaps);
        Assert.Contains("Relatorio consolidado", result.ReportMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Itens que exigem atencao", result.ReportMarkdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetItem_RetornaDetalheMinimoDaCorrecao()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.", "Nota privada para o professor.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);
        var sut = new GetAssistedGradingItemQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingItemQuery(item.Id, batch.Id),
            CancellationToken.None);

        Assert.Equal(item.Id, result.GradingItemId);
        Assert.Equal(batch.Id, result.BatchJobId);
        Assert.Equal("501", result.AssignmentId);
        Assert.Equal("9001", result.SubmissionId);
        Assert.Equal("101", result.StudentId);
        Assert.Equal(8m, result.SuggestedGrade);
        Assert.Equal(8.5m, result.FinalGrade);
        Assert.Equal("Nota privada para o professor.", result.PrivateNotesToTeacher);
        Assert.Equal("Feedback final revisado.", result.FinalFeedback);
        Assert.Equal("Reviewed", result.ReviewStatus);
        Assert.False(string.IsNullOrWhiteSpace(result.DraftVersionHash));
        Assert.Empty(result.PendingIssues);
    }

    [Fact]
    public async Task GetItem_ComBaixaConfianca_RetornaPendenciaEObservacaoPrivada()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(4m, 0.35m, "Rascunho de baixa confianca.", "Baixa confianca: texto curto.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingItemQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingItemQuery(item.Id, batch.Id),
            CancellationToken.None);

        Assert.Equal("Baixa confianca: texto curto.", result.PrivateNotesToTeacher);
        Assert.Contains(result.PendingIssues, issue => issue.Contains("Baixa confianca", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetItem_ComFalhaDeAnalise_RetornaPendenciaDeProcessamento()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAnalysisFailed("Falha simulada ao processar contexto.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingItemQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingItemQuery(item.Id, batch.Id),
            CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Equal("Falha simulada ao processar contexto.", result.PrivateNotesToTeacher);
        Assert.Contains(result.PendingIssues, issue => issue.Contains("Falha no processamento", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetItem_RetornaEvidenciasPorCriterio()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddEvidenceAsync(
            new GradingEvidence(
                Guid.NewGuid(),
                item.Id,
                "c1",
                "Descrever eventos de TI.",
                4m,
                3m,
                "O texto descreve monitoramento e alerta.",
                "Faltou exemplo operacional.",
                TeacherReviewRequired: true,
                CreatedAt: new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);
        var sut = new GetAssistedGradingItemQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var result = await sut.Handle(
            new GetAssistedGradingItemQuery(item.Id, batch.Id),
            CancellationToken.None);

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal("c1", evidence.CriterionId);
        Assert.Equal("Descrever eventos de TI.", evidence.CriterionText);
        Assert.Equal(4m, evidence.MaxPoints);
        Assert.Equal(3m, evidence.SuggestedPoints);
        Assert.Equal("O texto descreve monitoramento e alerta.", evidence.EvidenceText);
        Assert.Equal("Faltou exemplo operacional.", evidence.GapsText);
        Assert.True(evidence.TeacherReviewRequired);
    }

    [Fact]
    public async Task GetItem_QuandoBatchInformadoNaoCorresponde_DeveFalhar()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingItemQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(
                new GetAssistedGradingItemQuery(item.Id, Guid.Parse("00000000-0000-0000-0000-000000000999")),
                CancellationToken.None));

        Assert.Equal("O item informado nao pertence ao lote solicitado.", ex.Message);
    }

    [Fact]
    public async Task UpdateDraft_SalvaDecisaoProfessorERevisaoFinal()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var contextSnapshot = Snapshot(item);
        repository.Snapshots.Add(contextSnapshot);
        GradingContextIdentity.EnsureVersioned(item, contextSnapshot);
        var expectedDraftVersionHash = GradingDraftVersionHash.Compute(item);
        var auditLogs = new FakeAuditLogRepository();
        var sut = new UpdateAssistedGradingDraftCommandHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            auditLogs,
            new FakeMoodleAssignmentSettingsGateway());

        var result = await sut.Handle(
            new UpdateAssistedGradingDraftCommand(
                item.Id,
                FinalGrade: 8.5m,
                FinalFeedback: "Feedback final revisado.",
                TeacherDecision: "approved",
                ReviewNotes: "Ajustei a nota pela conclusao.",
                ExpectedReviewStatus: "NotReviewed",
                ExpectedDraftVersionHash: expectedDraftVersionHash),
            CancellationToken.None);

        Assert.Equal(item.Id, result.GradingItemId);
        Assert.Equal(8.5m, result.FinalGrade);
        Assert.Equal("Feedback final revisado.", result.FinalFeedback);
        Assert.Equal("approved", result.TeacherDecision);
        Assert.Equal("Ajustei a nota pela conclusao.", result.ReviewNotes);
        Assert.Equal("Reviewed", result.ReviewStatus);
        Assert.Equal("Pending", result.CommitStatus);
        Assert.False(string.IsNullOrWhiteSpace(result.DraftVersionHash));
        Assert.Empty(result.PendingIssues);
        Assert.Equal(1, repository.SaveChangesCount);
        var audit = Assert.Single(auditLogs.Logs);
        Assert.Equal("teacher-1", audit.ActorSubject);
        Assert.Equal("draft_reviewed", audit.Status);
        Assert.NotEqual(default, audit.CreatedAt);
        using var request = JsonDocument.Parse(audit.RequestSanitizedJson);
        Assert.Equal(8m, request.RootElement.GetProperty("previousReview").GetProperty("grade").GetDecimal());
        Assert.Equal("Rascunho.", request.RootElement.GetProperty("previousReview").GetProperty("feedback").GetString());
        Assert.Equal(8.5m, request.RootElement.GetProperty("finalGrade").GetDecimal());
        Assert.Equal("Feedback final revisado.", request.RootElement.GetProperty("finalFeedback").GetString());
        using var response = JsonDocument.Parse(audit.ResponseSummaryJson);
        Assert.Equal(8.5m, response.RootElement.GetProperty("finalGrade").GetDecimal());
        Assert.Equal("Feedback final revisado.", response.RootElement.GetProperty("finalFeedback").GetString());
    }

    [Fact]
    public async Task UpdateDraft_PermiteFeedbackSemNota()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(null, 0.8m, "Rascunho de feedback.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var contextSnapshot = Snapshot(item);
        repository.Snapshots.Add(contextSnapshot);
        GradingContextIdentity.EnsureVersioned(item, contextSnapshot);
        var expectedDraftVersionHash = GradingDraftVersionHash.Compute(item);
        var sut = new UpdateAssistedGradingDraftCommandHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeMoodleAssignmentSettingsGateway());

        var result = await sut.Handle(
            new UpdateAssistedGradingDraftCommand(
                GradingItemId: item.Id,
                FinalGrade: null,
                FinalFeedback: "Feedback final sem nota.",
                TeacherDecision: "approved",
                ReviewNotes: null,
                ExpectedReviewStatus: "NotReviewed",
                ExpectedDraftVersionHash: expectedDraftVersionHash),
            CancellationToken.None);

        Assert.Null(result.FinalGrade);
        Assert.Equal("Feedback final sem nota.", result.FinalFeedback);
        Assert.Equal("Reviewed", result.ReviewStatus);
        Assert.Equal("Pending", result.CommitStatus);
    }

    [Fact]
    public async Task UpdateDraft_RejeitaRevisaoSemHashDaVersaoLida()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Snapshots.Add(Snapshot(item));
        var sut = new UpdateAssistedGradingDraftCommandHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeMoodleAssignmentSettingsGateway());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(
                new UpdateAssistedGradingDraftCommand(
                    item.Id,
                    FinalGrade: 8.5m,
                    FinalFeedback: "Feedback revisado.",
                    TeacherDecision: "approved",
                    ReviewNotes: "Versao exigida.",
                    ExpectedReviewStatus: "NotReviewed"),
                CancellationToken.None));

        Assert.Contains("hash da versao lida", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GradingReviewStatus.NotReviewed, item.ReviewStatus);
        Assert.Null(item.FinalGrade);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    [Fact]
    public async Task UpdateDraft_RepetidoComMesmoPayload_RetornaResultadoSemDuplicarAlteracao()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321, "approved", "Ajustei a nota pela conclusao.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Snapshots.Add(Snapshot(item));
        var sut = new UpdateAssistedGradingDraftCommandHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeMoodleAssignmentSettingsGateway());

        var result = await sut.Handle(
            new UpdateAssistedGradingDraftCommand(
                item.Id,
                FinalGrade: 8.5m,
                FinalFeedback: "Feedback final revisado.",
                TeacherDecision: "approved",
                ReviewNotes: "Ajustei a nota pela conclusao.",
                ExpectedReviewStatus: "NotReviewed"),
            CancellationToken.None);

        Assert.Equal("Reviewed", result.ReviewStatus);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    [Fact]
    public async Task UpdateDraft_ComStatusEsperadoDivergenteEBpayloadDiferente_BloqueiaSobrescrita()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        item.ApplyTeacherReview(8.5m, "Feedback final revisado.", "teacher-1", 321, "approved", "Ajustei a nota.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Snapshots.Add(Snapshot(item));
        var sut = new UpdateAssistedGradingDraftCommandHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeMoodleAssignmentSettingsGateway());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.Handle(
                new UpdateAssistedGradingDraftCommand(
                    item.Id,
                    FinalGrade: 7m,
                    FinalFeedback: "Outro feedback.",
                    TeacherDecision: "needs_changes",
                    ReviewNotes: "Mudanca concorrente.",
                    ExpectedReviewStatus: "NotReviewed",
                    ExpectedDraftVersionHash: "stale-version"),
                CancellationToken.None));

        Assert.Equal("O rascunho foi alterado desde a ultima leitura. Consulte o item novamente antes de sobrescrever.", ex.Message);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    [Fact]
    public async Task GetBatchStatus_DeOutroCriadorSemEscopoAdmin_DeveFalhar()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = new GetAssistedGradingBatchStatusQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-2"));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sut.Handle(new GetAssistedGradingBatchStatusQuery(batch.Id, Page: 1, PageSize: 10), CancellationToken.None));

        Assert.Equal("Usuario atual nao esta autorizado a acessar este lote de correcao.", ex.Message);
    }

    [Fact]
    public async Task GetCoordinationReport_DeOutroCriadorSemEscopoAdmin_DeveFalhar()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = new GetAssistedGradingCoordinationReportQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-2"));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sut.Handle(new GetAssistedGradingCoordinationReportQuery(batch.Id), CancellationToken.None));

        Assert.Equal("Usuario atual nao esta autorizado a acessar este lote de correcao.", ex.Message);
    }

    [Fact]
    public async Task GetItem_DeOutroCriadorComEscopoAdmin_DevePermitir()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingItemQueryHandler(
            repository,
            new FakeCurrentUserContext("coordinator-1", ["grading.admin"]));

        var result = await sut.Handle(new GetAssistedGradingItemQuery(item.Id, batch.Id), CancellationToken.None);

        Assert.Equal(item.Id, result.GradingItemId);
    }

    [Fact]
    public async Task UpdateDraft_DeOutroCriadorSemEscopoAdmin_DeveFalhar()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.SetDraft(8m, 0.8m, "Rascunho.");
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new UpdateAssistedGradingDraftCommandHandler(
            repository,
            new FakeCurrentUserContext("teacher-2"),
            new FakeMoodleUserResolver(654),
            new FakeAuditLogRepository(),
            new FakeMoodleAssignmentSettingsGateway());

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sut.Handle(
                new UpdateAssistedGradingDraftCommand(
                    item.Id,
                    FinalGrade: 8.5m,
                    FinalFeedback: "Feedback final revisado.",
                    TeacherDecision: "approved",
                    ReviewNotes: null,
                    ExpectedReviewStatus: "NotReviewed"),
                CancellationToken.None));

        Assert.Equal("Usuario atual nao esta autorizado a acessar este lote de correcao.", ex.Message);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    private static CreateAssistedGradingBatchCommandHandler CreateHandler(
        FakeGradingReviewRepository repository,
        FakeMediator mediator,
        FakeAssignmentSubmissionsGateway? submissionsGateway = null) =>
        new(
            repository,
            mediator,
            new FakeCurrentUserContext("teacher-1"),
            new FakeMoodleUserResolver(321),
            new FakeAuditLogRepository(),
            new FakeGradingBatchOrchestrator(),
            new FakeCourseContentsGateway(),
            submissionsGateway ?? new FakeAssignmentSubmissionsGateway());

    private sealed class FakeGradingReviewRepository : IGradingReviewRepository
    {
        public List<AssistedGradingBatch> Batches { get; } = [];

        public List<AssistedGradingItem> Items { get; } = [];

        public List<GradingArtifact> Artifacts { get; } = [];

        public List<GradingEvidence> Evidence { get; } = [];
        public List<GradingContextSnapshotDocument> Snapshots { get; } = [];

        public bool FailListItemsByBatch { get; init; }

        public int SaveChangesCount { get; private set; }

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Batches.SingleOrDefault(batch => batch.Id == id));
        }

        public Task<AssistedGradingBatch?> GetBatchByIdempotencyKeyAsync(
            string createdBySubject,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(Batches.SingleOrDefault(batch =>
                batch.CreatedBySubject == createdBySubject &&
                batch.IdempotencyKey == idempotencyKey));

        public Task<IReadOnlyList<GradingSubmissionIdentity>> ListExistingSubmissionIdentitiesAsync(
            IReadOnlyCollection<GradingSubmissionIdentity> identities,
            string? moodleConnectionId,
            string? connectorClientId,
            string? connectionAlias,
            CancellationToken cancellationToken)
        {
            var expected = identities.ToHashSet();
            var batches = Batches
                .Where(batch => batch.Status != GradingBatchStatus.Cancelled)
                .ToDictionary(batch => batch.Id);
            var matches = Items
                .Where(item => item.SubmissionId is not null && batches.ContainsKey(item.BatchId))
                .Select(item => new GradingSubmissionIdentity(
                    item.CourseId,
                    item.AssignmentId,
                    item.SubmissionId!.Value,
                    item.AttemptNumber))
                .Where(expected.Contains)
                .Distinct()
                .ToArray();
            return Task.FromResult<IReadOnlyList<GradingSubmissionIdentity>>(matches);
        }

        public Task AddItemAsync(AssistedGradingItem item, CancellationToken cancellationToken)
        {
            Items.Add(item);
            return Task.CompletedTask;
        }

        public Task AddArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken)
        {
            Artifacts.Add(artifact);
            return Task.CompletedTask;
        }

        public Task UpdateArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken)
        {
            var index = Artifacts.FindIndex(existing => existing.Id == artifact.Id);
            if (index < 0)
            {
                throw new InvalidOperationException("Artifact nao encontrado no fake.");
            }

            Artifacts[index] = artifact;
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
            if (FailListItemsByBatch)
            {
                throw new InvalidOperationException("falha de leitura simulada");
            }

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
            return Task.FromResult<IReadOnlyList<GradingArtifact>>(Artifacts
                .Where(artifact => artifact.GradingItemId == gradingItemId)
                .ToArray());
        }

        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(
            Guid gradingItemId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<GradingEvidence>>(Evidence
                .Where(evidence => evidence.GradingItemId == gradingItemId)
                .ToArray());
        }

        public Task<IReadOnlyDictionary<Guid, GradingContextSnapshotDocument>> ListLatestContextSnapshotsByItemsAsync(IReadOnlyCollection<Guid> gradingItemIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, GradingContextSnapshotDocument>>(Snapshots
                .Where(snapshot => gradingItemIds.Contains(snapshot.GradingItemId))
                .GroupBy(snapshot => snapshot.GradingItemId)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.Version).First()));

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCount++;
            return Task.CompletedTask;
        }
    }

    private static GradingContextSnapshotDocument Snapshot(AssistedGradingItem item) =>
        GradingContextSnapshotDocument.FromSnapshot(GradingContextSnapshot.Create(
            item.Id,
            item.BatchId,
            new MoodleAssignmentReference(item.CourseId, item.AssignmentId, null),
            new MoodleSubmissionReference(item.SubmissionId ?? 1),
            new MoodleUserReference(item.MoodleUserId),
            item.AttemptNumber,
            1,
            $"Tarefa {item.AssignmentId}",
            null,
            [],
            null,
            new GradingScaleSnapshot(10m, null, null),
            [],
            [],
            new GradingExtractionSummary("succeeded", 0, false, 0, 0, null),
            new GradingEvidenceCoverage(0, 0, 0, 0, 0, 0, false),
            null,
            [],
            [],
            false));

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

    private sealed class FakeMoodleAssignmentSettingsGateway : IMoodleAssignmentSettingsGateway
    {
        public AssignmentSettingsSummary? Settings { get; init; }

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken)
            => Task.FromResult<AssignmentSettingsSummary?>(Settings ?? new AssignmentSettingsSummary(assignmentId, 10m));
    }

    private sealed class FakeMoodleUserResolver(long? moodleUserId) : IMoodleUserResolver
    {
        public Task<long?> ResolveMoodleUserIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(moodleUserId);
        }
    }

    private sealed class FakeCredentialsProvider(MoodleCredentials credentials)
        : IMoodleCredentialsProvider
    {
        public int CallCount { get; private set; }

        public Task<MoodleCredentials> GetCurrentCredentialsAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(credentials);
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
            => Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);

        public Task<int> CountByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<IReadOnlyList<MoodleAuditLog>> ListByBatchJobIdAsync(
            Guid batchJobId,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MoodleAuditLog>>([]);

        public Task<int> CountByBatchJobIdAsync(Guid batchJobId, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());        public Task SaveChangesAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeGradingBatchOrchestrator : IGradingBatchOrchestrator
    {
        public Guid? LastEnqueuedBatchId { get; private set; }

        public Guid? LastCancelledBatchId { get; private set; }

        public Task EnqueueAsync(Guid batchId, CancellationToken cancellationToken)
        {
            LastEnqueuedBatchId = batchId;
            return Task.CompletedTask;
        }

        public Task CancelAsync(Guid batchId, CancellationToken cancellationToken)
        {
            LastCancelledBatchId = batchId;
            var batch = BatchLookup?.Invoke(batchId);
            batch?.Cancel();
            return Task.CompletedTask;
        }

        public Func<Guid, AssistedGradingBatch?>? BatchLookup { get; set; }

        public Task<GradingBatchOrchestratorStatus> GetStatusAsync(Guid batchId, CancellationToken cancellationToken)
        {
            var batch = BatchLookup?.Invoke(batchId)
                ?? AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 0);
            return Task.FromResult(new GradingBatchOrchestratorStatus(
                batchId,
                batch.Status,
                batch.TotalItems,
                batch.ProcessedItems,
                batch.ReadyItems,
                batch.BlockedItems,
                batch.FailedItems,
                IsQueued: false,
                LastError: null));
        }
    }

    private sealed class FakeMediator : IMediator
    {
        public ListAssignmentSubmissionsQuery? LastListQuery { get; private set; }

        public int ListQueryCallCount { get; private set; }

        public int FailuresBeforeSuccess { get; init; }

        public ISet<string> FailAssignmentIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public bool UsePagedRows { get; init; }

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
            if (request is ListAssignmentSubmissionsQuery list)
            {
                LastListQuery = list;
                ListQueryCallCount++;
                if (FailAssignmentIds.Contains(list.AssignmentId) || ListQueryCallCount <= FailuresBeforeSuccess)
                {
                    throw new MoodleApiException(
                        MoodleErrorContract.NetworkError,
                        "falha de rede simulada");
                }
                return Task.FromResult((TResponse)(object)CreatePage(list, UsePagedRows));
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            if (request is ListAssignmentSubmissionsQuery list)
            {
                LastListQuery = list;
                return Task.FromResult<object?>(CreatePage(list, UsePagedRows));
            }

            throw new NotSupportedException($"Request nao suportado no fake mediator: {request.GetType().Name}");
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<object?>();

        private static AssignmentSubmissionsPage CreatePage(
            ListAssignmentSubmissionsQuery query,
            bool usePagedRows = false)
        {
            if (usePagedRows)
            {
                var allRows = Enumerable.Range(1, 8)
                    .Select(index => new AssignmentSubmissionSummary(
                        (100 + index).ToString(),
                        $"Aluno {index}",
                        (9000 + index).ToString(),
                        "submitted",
                        "notgraded",
                        Submitted: true,
                        Late: false,
                        NeedsGrading: true,
                        SubmittedAt: new DateTimeOffset(2026, 6, 10, index, 0, 0, TimeSpan.Zero),
                        ModifiedAt: new DateTimeOffset(2026, 6, 10, index, 0, 0, TimeSpan.Zero),
                        AttemptNumber: 0,
                        FileCount: 0,
                        HasOnlineText: true))
                    .ToArray();
                var pageRows = allRows
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize + 1)
                    .ToArray();

                return new AssignmentSubmissionsPage(
                    "10",
                    query.AssignmentId,
                    "42",
                    "Tarefa 1",
                    query.Page,
                    query.PageSize,
                    query.Filter,
                    query.IncludeLate,
                    query.IncludeUngraded,
                    query.Since,
                    query.Before,
                    Total: allRows.Length,
                    HasMore: pageRows.Length > query.PageSize,
                    pageRows.Take(query.PageSize).ToArray());
            }

            return new AssignmentSubmissionsPage(
                "10",
                query.AssignmentId,
                "42",
                "Tarefa 1",
                query.Page,
                query.PageSize,
                query.Filter,
                query.IncludeLate,
                query.IncludeUngraded,
                query.Since,
                query.Before,
                Total: 2,
                HasMore: false,
                [
                    new AssignmentSubmissionSummary(
                        "101",
                        "Ana Souza",
                        "9001",
                        "submitted",
                        "notgraded",
                        Submitted: true,
                        Late: false,
                        NeedsGrading: true,
                        SubmittedAt: new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero),
                        ModifiedAt: new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero),
                        AttemptNumber: 0,
                        FileCount: 1,
                        HasOnlineText: true,
                        Files:
                        [
                            new AssignmentSubmissionFile(
                                "entrega.txt",
                                "text/plain",
                                31,
                                "https://moodle.example/pluginfile.php/entrega.txt")
                        ]),
                    new AssignmentSubmissionSummary(
                        "102",
                        "Bruno Lima",
                        "9002",
                        "submitted",
                        "notgraded",
                        Submitted: true,
                        Late: false,
                        NeedsGrading: true,
                        SubmittedAt: new DateTimeOffset(2026, 6, 10, 11, 0, 0, TimeSpan.Zero),
                        ModifiedAt: new DateTimeOffset(2026, 6, 10, 11, 0, 0, TimeSpan.Zero),
                        AttemptNumber: 0,
                        FileCount: 0,
                        HasOnlineText: true)
                ]);
        }
    }

    private sealed class FakeSubmissionFileGateway : IMoodleSubmissionFileGateway
    {
        public string? LastFileUrl { get; private set; }

        public List<string> DownloadedFileUrls { get; } = [];

        public Task<SubmissionFileDownloadResult> DownloadFileAsync(
            string userExternalId,
            string fileUrl,
            string filename,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            LastFileUrl = fileUrl;
            DownloadedFileUrls.Add(fileUrl);
            return Task.FromResult(new SubmissionFileDownloadResult(
                filename,
                "text/plain",
                31,
                "sha-1",
                "Texto extraido real da submissao."u8.ToArray(),
                Truncated: false));
        }
    }

    private sealed class FakeCourseContentsGateway : IMoodleCourseContentsGateway
    {
        public int CallCount { get; private set; }

        public int FailuresBeforeSuccess { get; init; }

        public bool ReturnEmptyContents { get; init; }

        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount <= FailuresBeforeSuccess)
            {
                throw new MoodleApiException(
                    MoodleErrorContract.NetworkError,
                    "falha de rede simulada");
            }

            if (ReturnEmptyContents)
            {
                return Task.FromResult(new CourseContentsSummary(
                    courseId,
                    moduleTypes.ToArray(),
                    includeHidden,
                    onlyWithFiles,
                    Sections:
                    [
                        new CourseSectionSummary(
                            "1",
                            SectionNumber: 1,
                            "Atividade oficial",
                            Summary: null,
                            Visible: true,
                            ModuleCount: 1,
                            IsEmpty: false,
                            Modules:
                            [
                                new CourseModuleSummary(
                                    "42",
                                    InstanceId: "501",
                                    "assign",
                                    "Tarefa oficial",
                                    Url: null,
                                    Visible: true,
                                    UserVisible: true,
                                    Description: null,
                                    AvailabilityInfo: null,
                                    Dates: [],
                                    Files: [])
                            ])
                    ]));
            }

            return Task.FromResult(new CourseContentsSummary(
                courseId,
                moduleTypes.ToArray(),
                includeHidden,
                onlyWithFiles,
                Sections:
                [
                    new CourseSectionSummary(
                        "1",
                        SectionNumber: 1,
                        "SAP 01",
                        Summary: null,
                        Visible: true,
                        ModuleCount: 3,
                        IsEmpty: false,
                        Modules:
                        [
                            new CourseModuleSummary(
                                "499",
                                InstanceId: "900",
                                "resource",
                                "Calendario do curso",
                                Url: null,
                                Visible: true,
                                UserVisible: true,
                                Description: "Datas gerais.",
                                AvailabilityInfo: null,
                                Dates: [],
                                Files: []),
                            new CourseModuleSummary(
                                "500",
                                InstanceId: "901",
                                "resource",
                                "Orientacoes SAP 01 - Etapa 1",
                                Url: null,
                                Visible: true,
                                UserVisible: true,
                                Description: null,
                                AvailabilityInfo: null,
                                Dates: [],
                                Files:
                                [
                                    new CourseModuleFile(
                                        Type: "file",
                                        FileName: "Orientacoes SAP 01 - Etapa 1.pdf",
                                        FilePath: "/",
                                        FileSize: 100,
                                        MimeType: "application/pdf",
                                        FileUrl: "https://moodle.example/pluginfile.php/orientacoes.pdf",
                                        IsExternalFile: false)
                                ]),
                            new CourseModuleSummary(
                                "42",
                                InstanceId: "501",
                                "assign",
                                "Tarefa 1",
                                Url: null,
                                Visible: true,
                                UserVisible: true,
                                Description: "Descricao da tarefa SAP 01 etapa 1.",
                                AvailabilityInfo: null,
                                Dates: [],
                                Files: [])
                        ])
                ]));
        }
    }

    private sealed class FakeAssignmentSubmissionsGateway : IMoodleAssignmentSubmissionsGateway
    {
        public int CallCount { get; private set; }

        public string? LastStatus { get; private set; }

        public bool ThrowOnRead { get; init; }

        public bool ThrowOnIndividualRead { get; init; }

        public int IndividualCallCount { get; private set; }

        public List<string> IndividualAssignments { get; } = [];

        public IReadOnlyList<AssignmentSubmissionRecord>? IndividualResults { get; init; }

        public ISet<string> FailAssignmentIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public ISet<string> NotFoundAssignmentIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastStatus = status;
            if (ThrowOnRead)
            {
                throw new MoodleApiException(MoodleErrorContract.NetworkError, "falha de rede simulada", functionName: "mod_assign_get_submissions");
            }

            return Task.FromResult<IReadOnlyList<AssignmentSubmissionsBatch>>(assignmentIds
                .Select(assignmentId => FailAssignmentIds.Contains(assignmentId)
                    ? new AssignmentSubmissionsBatch(assignmentId, [], MoodleErrorContract.NetworkError, "falha de rede simulada")
                    : NotFoundAssignmentIds.Contains(assignmentId)
                        ? new AssignmentSubmissionsBatch(assignmentId, [], "assignment_not_found", "tarefa nao encontrada")
                    : new AssignmentSubmissionsBatch(assignmentId, CreateSubmissions()))
                .ToArray());
        }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            IndividualCallCount++;
            IndividualAssignments.Add(assignmentId);
            if (ThrowOnIndividualRead)
            {
                throw new MoodleApiException(MoodleErrorContract.NetworkError, "falha de rede simulada");
            }

            return Task.FromResult(IndividualResults ?? CreateSubmissions());
        }

        private static IReadOnlyList<AssignmentSubmissionRecord> CreateSubmissions() =>
        [
            new AssignmentSubmissionRecord(
                "9001", "101", "submitted", "notgraded", null, null, 0, 1, true,
                [new AssignmentSubmissionFile("entrega.txt", "text/plain", 31, "https://moodle.example/pluginfile.php/entrega.txt")]),
            new AssignmentSubmissionRecord(
                "9002", "102", "submitted", "notgraded", null, null, 0, 0, true)
        ];
    }

    private sealed class FakePublicationStatusRepository : IPendingMoodleActionRepository
    {
        public List<PendingMoodleAction> Actions { get; } = [];
        public int BulkListCallCount { get; private set; }

        public Task AddAsync(PendingMoodleAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }

        public Task<PendingMoodleAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Actions.SingleOrDefault(action => action.Id == id));

        public Task<IReadOnlyList<PendingMoodleAction>> ListGradingPublicationsByBatchIdAsync(
            Guid batchJobId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingMoodleAction>>(Actions.ToArray());

        public Task<IReadOnlyList<PendingMoodleAction>> ListGradingPublicationsByBatchIdsAsync(
            IReadOnlyCollection<Guid> batchJobIds,
            CancellationToken cancellationToken)
        {
            BulkListCallCount++;
            return Task.FromResult<IReadOnlyList<PendingMoodleAction>>(Actions.ToArray());
        }

        public Task<PendingActionConfirmationClaimResult> TryConfirmWithAuditAsync(
            Guid id,
            string confirmedBySubject,
            DateTimeOffset confirmedAt,
            MoodleAuditLog confirmationAudit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
