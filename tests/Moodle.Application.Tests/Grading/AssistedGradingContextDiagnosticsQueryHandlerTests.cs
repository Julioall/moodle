using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.Grading;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class AssistedGradingContextDiagnosticsQueryHandlerTests
{
    [Fact]
    public async Task Handle_RetornaDiagnosticoSanitizadoDosArtefatosDeContexto()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(29972, [101112], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 29972, 101112, 1178546, 356968, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "Calendario do curso.pdf",
                "application/pdf",
                "sha-calendar",
                SizeBytes: 100,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Datas gerais e recados administrativos.",
                SummaryRef: "section:2;distance:2",
                CreatedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "SAP 01.pdf",
                "application/pdf",
                "sha-sap",
                SizeBytes: 200,
                ExtractionStatus: "succeeded",
                ExtractedTextRef: "Enunciado da atividade SAP 01 etapa 1 com criterios de entrega.",
                SummaryRef: "section:2;distance:1",
                CreatedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);
        await repository.AddArtifactAsync(
            new GradingArtifact(
                Guid.NewGuid(),
                item.Id,
                "assignment_context",
                "imagem-contexto.png",
                "image/png",
                "sha-image",
                SizeBytes: 300,
                ExtractionStatus: "failed",
                ExtractedTextRef: null,
                SummaryRef: "section:2;distance:3",
                CreatedAt: DateTimeOffset.UtcNow),
            CancellationToken.None);

        var sut = new GetAssistedGradingContextDiagnosticsQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-1"),
            new HeuristicAssignmentContextSelectionService(),
            Options.Create(new GradingLimitsOptions
            {
                MaxTextCharsPerSubmission = 10_000
            }));

        var result = await sut.Handle(
            new GetAssistedGradingContextDiagnosticsQuery(item.Id, batch.Id),
            CancellationToken.None);

        Assert.Equal(item.Id, result.GradingItemId);
        Assert.Equal(batch.Id, result.BatchJobId);
        Assert.Equal("29972", result.CourseId);
        Assert.Equal("101112", result.AssignmentId);
        Assert.Equal("1178546", result.SubmissionId);
        Assert.Equal(3, result.AssignmentContextArtifactsCount);
        Assert.Equal(2, result.AssignmentContextExtractedArtifactsCount);
        Assert.Equal("SAP 01.pdf", result.SelectedContextFileName);
        Assert.Equal("SAP 01.pdf", result.SelectedAssignmentStatementSource);
        Assert.Contains("SAP 01.pdf", result.SelectedCourseMaterials);
        Assert.True(result.SelectedContextScore > 0);
        Assert.True(result.ExtractedContextChars > 0);
        Assert.True(result.ExtractedContextWords > 0);
        Assert.Contains(result.Artifacts, artifact => artifact.FileName == "SAP 01.pdf" && artifact.Selected);
        Assert.Contains(result.Artifacts, artifact => artifact.FileName == "imagem-contexto.png" && artifact.ExtractionStatus == "failed");
    }

    [Fact]
    public async Task Handle_DeOutroCriadorSemEscopoAdmin_DeveFalhar()
    {
        var repository = new FakeGradingReviewRepository();
        var batch = AssistedGradingBatch.Create(29972, [101112], "teacher-1", 321, totalItems: 1);
        var item = AssistedGradingItem.Create(batch.Id, 29972, 101112, 1178546, 356968, 0);
        await repository.AddBatchAsync(batch, CancellationToken.None);
        await repository.AddItemAsync(item, CancellationToken.None);
        var sut = new GetAssistedGradingContextDiagnosticsQueryHandler(
            repository,
            new FakeCurrentUserContext("teacher-2"),
            new HeuristicAssignmentContextSelectionService(),
            Options.Create(new GradingLimitsOptions()));

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sut.Handle(new GetAssistedGradingContextDiagnosticsQuery(item.Id, batch.Id), CancellationToken.None));

        Assert.Equal("Usuario atual nao esta autorizado a acessar este lote de correcao.", ex.Message);
    }

    private sealed class FakeGradingReviewRepository : IGradingReviewRepository
    {
        public List<AssistedGradingBatch> Batches { get; } = [];
        public List<AssistedGradingItem> Items { get; } = [];
        public List<GradingArtifact> Artifacts { get; } = [];
        public List<GradingEvidence> Evidence { get; } = [];

        public Task AddBatchAsync(AssistedGradingBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }

        public Task<AssistedGradingBatch?> GetBatchAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(Batches.SingleOrDefault(batch => batch.Id == id));

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
            => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));

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
            => Task.FromResult(Items.Count(item => item.BatchId == batchId));

        public Task<IReadOnlyList<GradingArtifact>> ListArtifactsByItemAsync(
            Guid gradingItemId,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GradingArtifact>>(Artifacts
                .Where(artifact => artifact.GradingItemId == gradingItemId)
                .ToArray());

        public Task<IReadOnlyList<GradingEvidence>> ListEvidenceByItemAsync(
            Guid gradingItemId,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GradingEvidence>>(Evidence
                .Where(evidence => evidence.GradingItemId == gradingItemId)
                .ToArray());

        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByStatusAsync(
            GradingBatchStatus status, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());
        public Task<IReadOnlyList<AssistedGradingBatch>> ListBatchesByCreatorAsync(string createdBySubject, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AssistedGradingBatch>>(Array.Empty<AssistedGradingBatch>());        public Task SaveChangesAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
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
}
