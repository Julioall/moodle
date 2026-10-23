using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Domain.Grading;
using Microsoft.Extensions.Logging;

namespace Moodle.Application.Tests.Grading;

public sealed class LocalGradingBatchOrchestratorTests
{
    private static IOptions<GradingLimitsOptions> DefaultLimits(int maxItems = 400)
    {
        return Options.Create(new GradingLimitsOptions { MaxBatchItems = maxItems });
    }

    [Fact]
    public async Task EnqueueAsync_ComLoteValido_NaoLancaExcecao()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = CreateSut(repository);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);
    }

    [Fact]
    public async Task EnqueueAsync_PropagaInstrucoesPersistidasAoContexto()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            teacherInstructions: "Use linguagem acolhedora.");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(), item.Id, "submission_file", "entrega.txt", "text/plain", "sha-1", 120,
            "succeeded", "Texto legivel da entrega.", null, DateTimeOffset.UtcNow));

        var contextBuilder = new CapturingContextBuilder();
        var sut = new LocalGradingBatchOrchestrator(
            repository,
            DefaultLimits(),
            new GradingItemProcessor(
                contextBuilder,
                new FakeGradingAnalysisService(),
                NullLogger<GradingItemProcessor>.Instance),
            NullLogger<LocalGradingBatchOrchestrator>.Instance);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        Assert.Equal("Use linguagem acolhedora.", contextBuilder.LastTeacherInstructions);
        Assert.True(contextBuilder.LastIncludeRubric);
        Assert.True(contextBuilder.LastIncludeSubmissionFiles);
        Assert.False(contextBuilder.LastIncludeCourseMaterials);
    }

    [Fact]
    public async Task EnqueueAsync_ComArtefatoExtraido_MarcaAwaitingAiEAtualizaContadores()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "entrega.txt",
            "text/plain",
            "sha-1",
            120,
            "succeeded",
            "Texto extraido da entrega com evidencias suficientes para parecer preliminar.",
            SummaryRef: null,
            new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero)));
        var sut = new LocalGradingBatchOrchestrator(
            repository,
            DefaultLimits(),
            new GradingItemProcessor(
                new FakeCompleteGradingContextBuilder(),
                new FakeGradingAnalysisService(),
                NullLogger<GradingItemProcessor>.Instance),
            NullLogger<LocalGradingBatchOrchestrator>.Instance);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        // IA-first: item fica em AwaitingAiAnalysis, sem nota/feedback heurístico
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, item.Status);
        Assert.Equal(GradingReviewStatus.NotReviewed, item.ReviewStatus);
        Assert.Equal(GradingCommitStatus.NotReady, item.CommitStatus);
        Assert.Null(item.SuggestedGrade);
        Assert.Null(item.DraftFeedback);
        Assert.Contains("Pre-validacao concluida", item.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[a-f0-9]{64}$", item.ContextHash);
        Assert.Equal(1, item.ContextVersion);
        Assert.Equal("complete", item.ContextStatus);
        Assert.Equal(GradingBatchStatus.Processing, batch.Status);
        Assert.Equal(0, batch.ProcessedItems);
        Assert.Equal(0, batch.ReadyItems);  // AwaitingAiAnalysis is not "ready" yet
        Assert.Equal(0, batch.BlockedItems);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    [Fact]
    public async Task EnqueueAsync_SemTextoExtraido_BloqueiaItemEAtualizaContadores()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = CreateSut(repository);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        Assert.Equal(GradingItemStatus.Blocked, item.Status);
        Assert.Contains("conteúdo legível", item.DraftFeedback, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GradingBatchStatus.ReadyForReview, batch.Status);
        Assert.Equal(1, batch.ProcessedItems);
        Assert.Equal(0, batch.ReadyItems);
        Assert.Equal(1, batch.BlockedItems);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    [Theory]
    [InlineData(ExtractionStatus.Pending)]
    [InlineData(ExtractionStatus.Failed)]
    public async Task ProcessItem_ComResourceOriginalEMcpAtivo_AguardaIaSemExtrairTexto(string extractionStatus)
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddArtifactAsync(new GradingArtifact(
            Guid.NewGuid(), item.Id, "submission_file", "entrega.pdf", "application/pdf", null, 120,
            extractionStatus, null, "pending_ingestion", DateTimeOffset.UtcNow,
            "https://moodle.example/pluginfile.php/entrega.pdf"), CancellationToken.None);

        var processor = new GradingItemProcessor(
            new FakeGradingContextBuilder(repository),
            new FakeGradingAnalysisService(),
            NullLogger<GradingItemProcessor>.Instance,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));

        await processor.ProcessItemAsync(item, repository, CancellationToken.None);

        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, item.Status);
        Assert.Contains("MCP Resource", item.PrivateNotesToTeacher, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnqueueAsync_ComSubmissaoCriteriosEValor_MarcaAwaitingAi()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(29972, [101112], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 29972, 101112, 1178546, 356968, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Artifacts.AddRange(
        [
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "submission_file",
                "entrega.pdf",
                "application/pdf",
                "sha-submission",
                1200,
                "succeeded",
                "O plano apresenta gerenciamento de eventos de TI, exemplos de incidentes e problemas, alem de acoes corretivas coerentes para reduzir impactos.",
                SummaryRef: null,
                CreatedAt: DateTimeOffset.UtcNow),
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "Enunciado SAP 01 - Etapa 1.pdf",
                "application/pdf",
                "sha-context",
                900,
                "succeeded",
                """
                Enunciado SAP 01 - Etapa 1.
                Valor: 16 pontos.
                Critérios de avaliação:
                - Descrever gerenciamento de eventos de TI.
                - Apresentar exemplos de incidentes e problemas.
                - Propor ações corretivas coerentes.
                """,
                SummaryRef: null,
                CreatedAt: DateTimeOffset.UtcNow)
        ]);
        var sut = new LocalGradingBatchOrchestrator(
            repository,
            DefaultLimits(),
            new GradingItemProcessor(
                new GradingContextBuilder(
                    repository,
                    Options.Create(new GradingLimitsOptions()),
                    new HeuristicAssignmentContextSelectionService(),
                    new FakeMoodleAssignmentSettingsGateway(),
                    new FakeCriteriaGenerationService()),
                new StructuredGradingAnalysisService(),
                NullLogger<GradingItemProcessor>.Instance),
            NullLogger<LocalGradingBatchOrchestrator>.Instance);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        // IA-first: item fica em AwaitingAiAnalysis, sem nota/feedback heurístico
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, item.Status);
        Assert.Null(item.SuggestedGrade);
        Assert.Null(item.DraftFeedback);
        Assert.Contains("Pre-validacao concluida", item.PrivateNotesToTeacher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnqueueAsync_ComAnalisePorCriterio_NaoPersiteEvidenciasHeuristicas()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "entrega.txt",
            "text/plain",
            "sha-1",
            120,
            "succeeded",
            "Texto da entrega com evidencia do criterio.",
            SummaryRef: null,
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = new LocalGradingBatchOrchestrator(
            repository,
            DefaultLimits(),
            new GradingItemProcessor(
                new FakeCompleteGradingContextBuilder(),
                new FakeGradingAnalysisService(),
                NullLogger<GradingItemProcessor>.Instance),
            NullLogger<LocalGradingBatchOrchestrator>.Instance);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        // IA-first: no heuristic evidence is persisted by the processor
        Assert.Empty(repository.Evidence);
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, item.Status);
    }

    [Fact]
    public async Task EnqueueAsync_QuandoUmItemFalha_ContinuaProcessandoDemaisItens()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var failedItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var readyItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(failedItem, CancellationToken.None);
        await repository.AddItemAsync(readyItem, CancellationToken.None);
        var sut = new LocalGradingBatchOrchestrator(
            repository,
            DefaultLimits(),
            new GradingItemProcessor(
                new FailingOneItemContextBuilder(failedItem.Id),
                new FakeGradingAnalysisService(),
                NullLogger<GradingItemProcessor>.Instance),
            NullLogger<LocalGradingBatchOrchestrator>.Instance);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        Assert.Equal(GradingItemStatus.Failed, failedItem.Status);
        Assert.Equal(GradingCommitStatus.NotReady, failedItem.CommitStatus);
        Assert.Contains("Falha ao processar", failedItem.DraftFeedback, StringComparison.OrdinalIgnoreCase);
        // IA-first: non-failed item goes to AwaitingAiAnalysis
        Assert.Equal(GradingItemStatus.AwaitingAiAnalysis, readyItem.Status);
        Assert.Equal(GradingBatchStatus.Processing, batch.Status);
        Assert.Equal(1, batch.ProcessedItems);
        Assert.Equal(0, batch.ReadyItems);  // AwaitingAiAnalysis not counted as ready
        Assert.Equal(1, batch.FailedItems);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    [Fact]
    public async Task EnqueueAsync_ComLoteEmProcessamento_RetomaSomenteItensPendentes()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 2);
        var alreadyProcessedItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        var pendingItem = AssistedGradingItem.Create(batch.Id, 10, 501, 9002, 102, 0);
        alreadyProcessedItem.SetDraft(null, 0m, "Rascunho anterior.");
        batch.UpdateCounters(processedItems: 1, readyItems: 1, blockedItems: 0, failedItems: 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(alreadyProcessedItem, CancellationToken.None);
        await repository.AddItemAsync(pendingItem, CancellationToken.None);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            pendingItem.Id,
            "submission_file",
            "entrega.txt",
            "text/plain",
            "sha-2",
            120,
            "succeeded",
            "Texto pendente extraido para retomada do lote.",
            SummaryRef: null,
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = CreateSut(repository);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        Assert.Equal("Rascunho anterior.", alreadyProcessedItem.DraftFeedback);
        // IA-first: pending item blocked because FakeGradingContextBuilder has no artifacts for it,
        // leading to empty text → Blocked status. But the previously processed item retains its draft.
        Assert.Equal(GradingBatchStatus.Processing, batch.Status);
        Assert.Equal(1, batch.ProcessedItems);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    [Fact]
    public async Task EnqueueAsync_ComLoteCancelado_NaoProcessaItensPendentes()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        batch.Cancel();
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        repository.Artifacts.Add(new GradingArtifact(
            Guid.NewGuid(),
            item.Id,
            "submission_file",
            "entrega.txt",
            "text/plain",
            "sha-1",
            120,
            "succeeded",
            "Texto que nao deve ser processado porque o lote foi cancelado.",
            SummaryRef: null,
            CreatedAt: DateTimeOffset.UtcNow));
        var sut = CreateSut(repository);

        await sut.EnqueueAsync(batch.Id, CancellationToken.None);

        Assert.Equal(GradingItemStatus.Pending, item.Status);
        Assert.Equal(GradingBatchStatus.Cancelled, batch.Status);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    [Fact]
    public async Task LoadAllBatchItemsAsync_PaginaLotesAcimaDoLimiteDoRepositorio()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 250);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        for (var index = 0; index < 250; index++)
        {
            await repository.AddItemAsync(
                AssistedGradingItem.Create(batch.Id, 10, 501, 9000 + index, 1000 + index, 0),
                CancellationToken.None);
        }

        var items = await GradingItemProcessor.LoadAllBatchItemsAsync(
            repository,
            batch.Id,
            CancellationToken.None,
            pageSize: 400);

        Assert.Equal(250, items.Count);
        Assert.Equal(250, items.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public async Task EnqueueAsync_ComBatchIdVazio_LancaArgumentException()
    {
        var sut = new LocalGradingBatchOrchestrator(
            new FakeGradingReviewRepository(),
            DefaultLimits(),
            new GradingItemProcessor(
                new FakeGradingContextBuilder(new FakeGradingReviewRepository()),
                new FakeGradingAnalysisService(),
                NullLogger<GradingItemProcessor>.Instance),
            NullLogger<LocalGradingBatchOrchestrator>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.EnqueueAsync(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueAsync_ComTotalItensSuperandoLimite_LancaInvalidOperationException()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 5);
        await repository.AddBatchAsync(batch, CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9000 + i, 100 + i, 0);
            await repository.AddItemAsync(item, CancellationToken.None);
        }

        var sut = new LocalGradingBatchOrchestrator(
            repository,
            DefaultLimits(maxItems: 2),
            new GradingItemProcessor(
                new FakeGradingContextBuilder(repository),
                new FakeGradingAnalysisService(),
                NullLogger<GradingItemProcessor>.Instance),
            NullLogger<LocalGradingBatchOrchestrator>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.EnqueueAsync(batch.Id, CancellationToken.None));

        Assert.Contains("limite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelAsync_ComLotePendente_AlteraStatusParaCancelled()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = CreateSut(repository);

        await sut.CancelAsync(batch.Id, CancellationToken.None);

        Assert.Equal(GradingBatchStatus.Cancelled, batch.Status);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    [Fact]
    public async Task CancelAsync_ComLoteJaCancelado_NaoSalvaAlteracoes()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 1);
        batch.Cancel();
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = CreateSut(repository);

        await sut.CancelAsync(batch.Id, CancellationToken.None);

        Assert.Equal(0, repository.SaveChangesCount);
    }

    [Fact]
    public async Task GetStatusAsync_RetornaStatusAtualDoLote()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, totalItems: 3);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        var sut = CreateSut(repository);

        var status = await sut.GetStatusAsync(batch.Id, CancellationToken.None);

        Assert.Equal(batch.Id, status.BatchId);
        Assert.Equal(GradingBatchStatus.Pending, status.BatchStatus);
        Assert.Equal(3, status.TotalItems);
        Assert.True(status.IsQueued);
    }

    private sealed class FakeGradingReviewRepository : IGradingReviewRepository
    {
        public List<AssistedGradingBatch> Batches { get; } = [];
        public List<AssistedGradingItem> Items { get; } = [];
        public List<GradingArtifact> Artifacts { get; } = [];
        public List<GradingEvidence> Evidence { get; } = [];
        public int SaveChangesCount { get; private set; }

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(Batches.SingleOrDefault(b => b.Id == id));

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

        public Task AddEvidenceAsync(GradingEvidence evidence, CancellationToken cancellationToken)
        {
            Evidence.Add(evidence);
            return Task.CompletedTask;
        }

        public Task<AssistedGradingItem?> GetItemAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(Items.SingleOrDefault(i => i.Id == id));

        public Task<IReadOnlyList<AssistedGradingItem>> ListItemsByBatchAsync(
            Guid batchId, int page, int pageSize, CancellationToken cancellationToken)
        {
            var result = Items
                .Where(i => i.BatchId == batchId)
                .Skip((Math.Max(1, page) - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult<IReadOnlyList<AssistedGradingItem>>(result);
        }

        public Task<int> CountItemsByBatchAsync(Guid batchId, CancellationToken cancellationToken)
            => Task.FromResult(Items.Count(i => i.BatchId == batchId));

        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(
            Guid gradingItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GradingArtifact>>(Artifacts
                .Where(artifact => artifact.GradingItemId == gradingItemId)
                .ToArray());

        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(
            Guid gradingItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GradingEvidence>>(Evidence
                .Where(evidence => evidence.GradingItemId == gradingItemId)
                .ToArray());

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Batches
                .Where(b => b.Status == status)
                .ToArray());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveChangesCount++;
            return Task.CompletedTask;
        }
    }

    private static LocalGradingBatchOrchestrator CreateSut(
        FakeGradingReviewRepository repository,
        int maxItems = 400)
    {
        return new LocalGradingBatchOrchestrator(
            repository,
            DefaultLimits(maxItems),
            new GradingItemProcessor(
                new FakeGradingContextBuilder(repository),
                new FakeGradingAnalysisService(),
                NullLogger<GradingItemProcessor>.Instance),
            NullLogger<LocalGradingBatchOrchestrator>.Instance);
    }

    private sealed class FakeGradingContextBuilder(FakeGradingReviewRepository repository) : IGradingContextBuilder
    {
        public async Task<GradingContext> BuildAsync(
            AssistedGradingItem item,
            GradingContextOptions options,
            CancellationToken cancellationToken)
        {
            var artifacts = await repository.ListArtifactsByItemAsync(item.Id, cancellationToken);
            var text = artifacts
                .Where(artifact => artifact.ExtractionStatus == "succeeded")
                .Select(artifact => artifact.ExtractedTextRef)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            return GradingContext.Build(
                item.Id,
                item.BatchId,
                item.CourseId.ToString(),
                item.AssignmentId.ToString(),
                item.SubmissionId?.ToString(),
                item.MoodleUserId.ToString(),
                assignmentStatement: null,
                criteria: null,
                rubricDescription: null,
                maxGrade: null,
                gradeScale: null,
                submissionText: text,
                attachedFiles: string.IsNullOrWhiteSpace(text)
                    ? []
                    : [new GradingFileInfo("entrega.txt", "text/plain", 120, "sha-1", text, true)],
                courseMaterials: null,
                teacherInstructions: options.TeacherInstructions);
        }
    }

    private sealed class CapturingContextBuilder : IGradingContextBuilder
    {
        public string? LastTeacherInstructions { get; private set; }
        public bool? LastIncludeRubric { get; private set; }
        public bool? LastIncludeSubmissionFiles { get; private set; }
        public bool? LastIncludeCourseMaterials { get; private set; }

        public Task<GradingContext> BuildAsync(
            AssistedGradingItem item,
            GradingContextOptions options,
            CancellationToken cancellationToken)
        {
            LastTeacherInstructions = options.TeacherInstructions;
            LastIncludeRubric = options.IncludeRubric;
            LastIncludeSubmissionFiles = options.IncludeSubmissionFiles;
            LastIncludeCourseMaterials = options.IncludeCourseMaterials;
            return Task.FromResult(GradingContext.Build(
                item.Id,
                item.BatchId,
                item.CourseId.ToString(),
                item.AssignmentId.ToString(),
                item.SubmissionId?.ToString(),
                item.MoodleUserId.ToString(),
                assignmentStatement: "Enunciado.",
                criteria: "Criterio.",
                rubricDescription: null,
                maxGrade: 10m,
                gradeScale: null,
                submissionText: "Texto legivel da entrega.",
                attachedFiles: [],
                courseMaterials: null,
                teacherInstructions: options.TeacherInstructions));
        }
    }

    private sealed class FakeCompleteGradingContextBuilder : IGradingContextBuilder
    {
        public Task<GradingContext> BuildAsync(
            AssistedGradingItem item,
            GradingContextOptions options,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(GradingContext.Build(
                item.Id,
                item.BatchId,
                item.CourseId.ToString(),
                item.AssignmentId.ToString(),
                item.SubmissionId?.ToString(),
                item.MoodleUserId.ToString(),
                assignmentStatement: "Enunciado.",
                criteria: "Criterio avaliado.",
                rubricDescription: null,
                maxGrade: 4m,
                gradeScale: null,
                submissionText: "Texto da entrega com evidencia do criterio.",
                attachedFiles: [new GradingFileInfo("entrega.txt", "text/plain", 120, "sha-1", "Texto da entrega com evidencia do criterio.", true)],
                courseMaterials: null,
                teacherInstructions: options.TeacherInstructions));
        }
    }

    private sealed class FailingOneItemContextBuilder(Guid failedItemId) : IGradingContextBuilder
    {
        public Task<GradingContext> BuildAsync(
            AssistedGradingItem item,
            GradingContextOptions options,
            CancellationToken cancellationToken)
        {
            if (item.Id == failedItemId)
            {
                throw new InvalidOperationException("Falha simulada no contexto.");
            }

            return Task.FromResult(GradingContext.Build(
                item.Id,
                item.BatchId,
                item.CourseId.ToString(),
                item.AssignmentId.ToString(),
                item.SubmissionId?.ToString(),
                item.MoodleUserId.ToString(),
                assignmentStatement: "Enunciado.",
                criteria: "Criterio avaliado.",
                rubricDescription: null,
                maxGrade: 4m,
                gradeScale: null,
                submissionText: "Texto da entrega com evidencia do criterio.",
                attachedFiles: [new GradingFileInfo("entrega.txt", "text/plain", 120, "sha-1", "Texto da entrega com evidencia do criterio.", true)],
                courseMaterials: null,
                teacherInstructions: options.TeacherInstructions));
        }
    }

    private sealed class FakeGradingAnalysisService : IGradingAnalysisService
    {
        public Task<GradingAnalysisResult> AnalyzeAsync(
            GradingAnalysisRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new GradingAnalysisResult(
                SuggestedGrade: null,
                Confidence: 0m,
                AnalysisStatus.AwaitingAiAnalysis,
                FeedbackToStudent: null,
                PrivateNotesToTeacher: "Pre-validacao concluida. Item pronto para analise via IA.",
                CriterionAnalysis: [],
                Blocks: []));
        }
    }
    internal sealed class FakeMoodleAssignmentSettingsGateway : IMoodleAssignmentSettingsGateway
    {
        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId, string courseId, string assignmentId, CancellationToken cancellationToken)
            => Task.FromResult<AssignmentSettingsSummary?>(null);
    }

    internal sealed class FakeCriteriaGenerationService : ICriteriaGenerationService
    {
        public Task<CriteriaGenerationResult> GenerateAsync(
            CriteriaGenerationRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new CriteriaGenerationResult("fake", request.MaxGrade, 0m, [], [], null));
    }
}
