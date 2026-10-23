using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingRunAggregationTests
{
    [Fact]
    public async Task PrepareRun_PaginaDezMilItensSemCarregarResourcesForaDaPagina()
    {
        var repository = new AggregateRepository();
        var run = GradingRun.Create("teacher-1");
        await repository.AddGradingRunAsync(run, CancellationToken.None);

        for (var batchIndex = 0; batchIndex < 25; batchIndex++)
        {
            var batch = AssistedGradingBatch.Create(
                10,
                [501],
                "teacher-1",
                321,
                totalItems: 400,
                gradingRunId: run.Id);
            await repository.AddBatchAsync(batch, CancellationToken.None);
            for (var itemIndex = 0; itemIndex < 400; itemIndex++)
            {
                var item = AssistedGradingItem.Create(
                    batch.Id,
                    10,
                    501,
                    100000L + batchIndex * 400 + itemIndex,
                    200000L + batchIndex * 400 + itemIndex,
                    0);
                item.MarkAwaitingAiAnalysis(null);
                await repository.AddItemAsync(item, CancellationToken.None);
                await repository.AddArtifactAsync(
                    new GradingArtifact(
                        Guid.NewGuid(),
                        item.Id,
                        "submission_file",
                        "resposta.txt",
                        "text/plain",
                        null,
                        10,
                        ExtractionStatus.Failed,
                        null,
                        "pending_resource",
                        DateTimeOffset.UtcNow,
                        $"https://moodle.example/file/{item.Id:N}"),
                    CancellationToken.None);
            }
        }

        var resourceGateway = new CountingResourceGateway();
        var handler = new PrepareAiGradingBatchQueryHandler(
            repository,
            new AggregateCurrentUser("teacher-1"),
            new AggregateSettingsGateway(),
            resourceGateway: resourceGateway,
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));

        var result = await handler.Handle(
            new PrepareAiGradingBatchQuery(run.Id, Page: 24, PageSize: 400),
            CancellationToken.None);

        Assert.Equal(run.Id, result.GradingRunId);
        Assert.Equal(10000, result.TotalItems);
        Assert.Equal(400, result.Items.Count);
        Assert.True(result.HasMore);
        Assert.Equal(25, result.NextPage);
        Assert.Equal(400, resourceGateway.RegisteredCount);
    }

    [Fact]
    public async Task PrepareRun_NaoPulaPaginaQuandoPaginaAnteriorViraRascunho()
    {
        var repository = new AggregateRepository();
        var run = GradingRun.Create("teacher-1");
        await repository.AddGradingRunAsync(run, CancellationToken.None);
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 800,
            gradingRunId: run.Id);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        for (var index = 0; index < 800; index++)
        {
            var item = AssistedGradingItem.Create(
                batch.Id,
                10,
                501,
                300000 + index,
                400000 + index,
                0);
            item.MarkAwaitingAiAnalysis(null);
            await repository.AddItemAsync(item, CancellationToken.None);
            await repository.AddArtifactAsync(
                new GradingArtifact(
                    Guid.NewGuid(),
                    item.Id,
                    "submission_file",
                    "resposta.txt",
                    "text/plain",
                    null,
                    10,
                    ExtractionStatus.Failed,
                    null,
                    "pending_resource",
                    DateTimeOffset.UtcNow,
                    $"https://moodle.example/file/{item.Id:N}"),
                CancellationToken.None);
        }

        var handler = new PrepareAiGradingBatchQueryHandler(
            repository,
            new AggregateCurrentUser("teacher-1"),
            new AggregateSettingsGateway(),
            resourceGateway: new CountingResourceGateway(),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));

        var firstPage = await handler.Handle(
            new PrepareAiGradingBatchQuery(run.Id, Page: 1, PageSize: 400),
            CancellationToken.None);
        Assert.Equal(400, firstPage.Items.Count);

        foreach (var item in repository.Items.Take(400))
        {
            item.SetDraft(8m, .9m, "Feedback salvo.");
        }

        var secondPage = await handler.Handle(
            new PrepareAiGradingBatchQuery(run.Id, Page: 2, PageSize: 400),
            CancellationToken.None);

        Assert.Equal(400, secondPage.TotalItems);
        Assert.Equal(400, secondPage.EligibleItems);
        Assert.Equal(400, secondPage.ItemsExcludedByStatus);
        Assert.Equal(400, secondPage.ItemsOnPage);
        Assert.Equal(400, secondPage.Items.Count);
        Assert.Equal("package_zip", secondPage.RecommendedTransport);
        Assert.Equal("prepare_packages", secondPage.NextAction);
        Assert.Contains(secondPage.Warnings, warning => warning.Contains("fora da fila", StringComparison.Ordinal));
        Assert.Equal(
            repository.Items.Skip(400).Take(400).Select(item => item.Id),
            secondPage.Items.Select(item => item.GradingItemId));
    }

    [Fact]
    public async Task PrepareRun_ExplicitaCoberturaIncompletaEmVezDeParecerCompleto()
    {
        var repository = new AggregateRepository();
        var run = GradingRun.Create("teacher-1");
        run.SetExpectedCoverage(expectedItemCount: 3, expectedBatchCount: 2);
        await repository.AddGradingRunAsync(run, CancellationToken.None);
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 2,
            gradingRunId: run.Id);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        for (var index = 0; index < 2; index++)
        {
            var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001 + index, 101 + index, 0);
            item.MarkAwaitingAiAnalysis(null);
            await repository.AddItemAsync(item, CancellationToken.None);
            await repository.AddArtifactAsync(
                new GradingArtifact(
                    Guid.NewGuid(), item.Id, "submission_file", "resposta.txt", "text/plain",
                    null, 10, ExtractionStatus.Failed, null, "pending_resource", DateTimeOffset.UtcNow,
                    $"https://moodle.example/file/{item.Id:N}"),
                CancellationToken.None);
        }

        var handler = new PrepareAiGradingBatchQueryHandler(
            repository,
            new AggregateCurrentUser("teacher-1"),
            new AggregateSettingsGateway(),
            resourceGateway: new CountingResourceGateway(),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));

        var result = await handler.Handle(new PrepareAiGradingBatchQuery(run.Id), CancellationToken.None);

        Assert.Equal(3, result.ExpectedItems);
        Assert.Equal(2, result.PreparedItems);
        Assert.Equal(1, result.MissingItems);
        Assert.Equal(1, result.MissingBatchCount);
        Assert.False(result.DecisionSafe);
        Assert.Contains(result.Warnings, warning => warning.Contains("Cobertura incompleta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunScope_NaoPermiteAcessoPorOutroUsuario()
    {
        var repository = new AggregateRepository();
        var run = GradingRun.Create("teacher-1");
        await repository.AddGradingRunAsync(run, CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            GradingBatchScopeResolver.ResolveAsync(
                repository,
                new AggregateCurrentUser("teacher-2"),
                run.Id,
                CancellationToken.None));
    }

    [Fact]
    public async Task RunDestination_NaoPodeAlternarEntreCsvEPublicacao()
    {
        var repository = new AggregateRepository();
        var run = GradingRun.Create("teacher-1");
        await repository.AddGradingRunAsync(run, CancellationToken.None);
        IGradingReviewRepository store = repository;

        Assert.True(await store.TrySetGradingRunDestinationAsync(run.Id, "csv", CancellationToken.None));
        Assert.False(await store.TrySetGradingRunDestinationAsync(run.Id, "publish", CancellationToken.None));
        Assert.Equal("csv", run.Destination);
    }

    [Fact]
    public async Task BatchLegadoResolveRunParaCompartilharMutexDeDestino()
    {
        var repository = new AggregateRepository();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            gradingRunId: run.Id);
        await repository.AddGradingRunAsync(run, CancellationToken.None);
        await repository.AddBatchAsync(batch, CancellationToken.None);

        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            new AggregateCurrentUser("teacher-1"),
            batch.Id,
            CancellationToken.None);

        Assert.Null(scope.Run);
        Assert.Equal(run.Id, scope.DestinationRun?.Id);
    }

    [Fact]
    public async Task PrepareBatchFilho_RetornaGradingRunPaiReal()
    {
        var repository = new AggregateRepository();
        var run = GradingRun.Create("teacher-1");
        var batch = AssistedGradingBatch.Create(
            10,
            [501],
            "teacher-1",
            321,
            totalItems: 1,
            gradingRunId: run.Id);
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 0);
        item.MarkAwaitingAiAnalysis(null);
        await repository.AddGradingRunAsync(run, CancellationToken.None);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(), item.Id, "submission_file", "resposta.txt", "text/plain",
                null, 10, ExtractionStatus.Failed, null, "pending_resource", DateTimeOffset.UtcNow,
                "https://moodle.example/file/9001"),
            CancellationToken.None);
        var handler = new PrepareAiGradingBatchQueryHandler(
            repository,
            new AggregateCurrentUser("teacher-1"),
            new AggregateSettingsGateway(),
            resourceGateway: new CountingResourceGateway(),
            resourceFeatures: Options.Create(new MoodleUniversalApiFeatureOptions
            {
                McpResourceSubmissionDeliveryEnabled = true
            }));

        var result = await handler.Handle(new PrepareAiGradingBatchQuery(batch.Id), CancellationToken.None);

        Assert.Equal(run.Id, result.GradingRunId);
    }

    private sealed class AggregateCurrentUser(string subject) : ICurrentUserContext
    {
        public string Subject { get; } = subject;
        public string? Email => null;
        public IReadOnlyCollection<string> Scopes => [];
        public bool HasScope(string scope) => false;
    }

    private sealed class AggregateSettingsGateway : IMoodleAssignmentSettingsGateway
    {
        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AssignmentSettingsSummary?>(new AssignmentSettingsSummary(assignmentId, 10m, "Atividade"));
    }

    private sealed class CountingResourceGateway : IMoodleResourceGateway
    {
        public int RegisteredCount { get; private set; }

        public Task<MoodleResourceDescriptor> RegisterAsync(MoodleResourceRegistration request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("O teste usa o caminho bulk.");

        public Task<IReadOnlyList<MoodleResourceDescriptor>> RegisterManyAsync(
            IReadOnlyList<MoodleResourceRegistration> requests,
            CancellationToken cancellationToken)
        {
            RegisteredCount += requests.Count;
            return Task.FromResult<IReadOnlyList<MoodleResourceDescriptor>>(
                requests.Select(request => new MoodleResourceDescriptor(
                    "moodle://resource/0123456789abcdef0123456789abcdef",
                    request.Filename,
                    request.MimeType,
                    request.SizeBytes,
                    request.Sha256)).ToArray());
        }

        public Task<MoodleResourceReadResult> ReadAsync(string uri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MoodleResourceDescriptor>> ExpandZipAsync(string uri, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoodleResourceDescriptor>>([]);
    }

    private sealed class AggregateRepository : IGradingReviewRepository
    {
        private readonly List<GradingRun> _runs = [];
        private readonly List<AssistedGradingBatch> _batches = [];
        private readonly List<AssistedGradingItem> _items = [];
        private readonly List<GradingArtifact> _artifacts = [];

        public IReadOnlyList<AssistedGradingItem> Items => _items;

        public Task AddGradingRunAsync(GradingRun run, CancellationToken cancellationToken) { _runs.Add(run); return Task.CompletedTask; }
        public Task<GradingRun?> GetGradingRunAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_runs.SingleOrDefault(run => run.Id == id));
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByGradingRunAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(_batches.Where(batch => batch.GradingRunId == id).OrderBy(batch => batch.Id).ToArray());
        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken) { _batches.Add(batch); return Task.CompletedTask; }
        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_batches.SingleOrDefault(batch => batch.Id == id));
        public Task AddItemAsync(AssistedGradingItem item, CancellationToken cancellationToken) { _items.Add(item); return Task.CompletedTask; }
        public Task AddArtifactAsync(GradingArtifact artifact, CancellationToken cancellationToken) { _artifacts.Add(artifact); return Task.CompletedTask; }
        public Task AddEvidenceAsync(GradingEvidence evidence, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AssistedGradingItem?> GetItemAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_items.SingleOrDefault(item => item.Id == id));
        public Task<IReadOnlyDictionary<Guid, AssistedGradingItem>> GetItemsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<Guid, AssistedGradingItem>>(_items.Where(item => ids.Contains(item.Id)).ToDictionary(item => item.Id));
        public Task<IReadOnlyList<AssistedGradingItem>> ListItemsByBatchAsync(Guid batchId, int page, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingItem>>(_items.Where(item => item.BatchId == batchId).OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArray());
        public Task<int> CountItemsByBatchAsync(Guid batchId, CancellationToken cancellationToken) => Task.FromResult(_items.Count(item => item.BatchId == batchId));
        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingArtifact>>(_artifacts.Where(artifact => artifact.GradingItemId == id).ToArray());
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<GradingArtifact>>> ListArtifactsByItemsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<GradingArtifact>>>(_artifacts.Where(artifact => ids.Contains(artifact.GradingItemId)).GroupBy(artifact => artifact.GradingItemId).ToDictionary(group => group.Key, group => (IReadOnlyList<GradingArtifact>)group.ToArray()));
        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GradingEvidence>>([]);
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<GradingEvidence>>> ListEvidenceByItemsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<GradingEvidence>>>(new Dictionary<Guid, IReadOnlyList<GradingEvidence>>());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(GradingBatchStatus status, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>([]);
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>([]);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
