using Moodle.Domain.Grading;
using System.Security.Cryptography;
using System.Text;

namespace Moodle.Application.Tests.Domain;

public sealed class GradingContextSnapshotTests
{
    [Fact]
    public void Create_GeraHashDeterministicoIndependenteDoHorarioDePublicacao()
    {
        var first = CreateSnapshot(DateTimeOffset.Parse("2026-08-27T10:00:00Z"));
        var second = CreateSnapshot(DateTimeOffset.Parse("2026-08-27T11:00:00Z"));

        Assert.Matches("^[a-f0-9]{64}$", first.ContextHash);
        Assert.Equal(first.ContextHash, second.ContextHash);
        Assert.Equal(first.ContextHash, GradingContextSnapshot.ComputeHash(first));
    }

    [Fact]
    public void Create_AlteraHashQuandoConteudoOuVersaoMudam()
    {
        var original = CreateSnapshot();
        var changedContent = CreateSnapshot(teacherInstructions: "Instrução revisada.");
        var changedVersion = CreateSnapshot(version: 2);

        Assert.NotEqual(original.ContextHash, changedContent.ContextHash);
        Assert.NotEqual(original.ContextHash, changedVersion.ContextHash);
    }

    [Fact]
    public void ComputeCanonicalPayloadHash_ContinuaLendoSnapshotSchemaV1SemMetadadosNovosDeEscala()
    {
        var canonicalV1 = GradingContextSnapshot.SerializeCanonicalPayload(CreateSnapshot())
            .Replace("\"SchemaVersion\":\"3\"", "\"SchemaVersion\":\"1\"", StringComparison.Ordinal);

        var hash = GradingContextSnapshot.ComputeCanonicalPayloadHash(canonicalV1);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalV1))).ToLowerInvariant();

        Assert.Equal(expected, hash);
    }

    [Fact]
    public void DeserializeCanonicalPayload_ContinuaLendoSnapshotV2SemMetadadosDeRubrica()
    {
        var legacy = CreateSnapshot(schemaVersion: "2");
        var payload = GradingContextSnapshot.SerializeCanonicalPayload(legacy);

        var restored = GradingContextSnapshot.DeserializeCanonicalPayload(payload);

        Assert.Equal("2", restored.SchemaVersion);
        Assert.Equal(legacy.ContextHash, restored.ContextHash);
        Assert.Null(restored.Rubric!.DefinitionId);
        Assert.Null(restored.Rubric.Criteria);
    }

    [Fact]
    public void Create_CopiaColecoesEImpedeMutacaoDoSnapshotPublicado()
    {
        var criteria = new List<GradingCriterionSnapshot>
        {
            new("criterion-1", "Argumentação", 10m, "rubric", "rubric-1")
        };
        var warnings = new List<string> { "Cobertura parcial." };
        var snapshot = CreateSnapshot(criteria: criteria, warnings: warnings);
        var originalHash = snapshot.ContextHash;

        criteria[0] = new("criterion-2", "Outro critério", 10m, "rubric", "rubric-2");
        warnings[0] = "Texto alterado externamente.";

        Assert.Equal(originalHash, snapshot.ContextHash);
        Assert.Equal("criterion-1", snapshot.Criteria[0].CriterionId);
        Assert.Equal("Cobertura parcial.", snapshot.Warnings[0]);
    }

    [Fact]
    public void Create_CopiaReferenciasDeArtefatoDentroDasEvidencias()
    {
        var artifactIds = new List<Guid>
        {
            Guid.Parse("44444444-4444-4444-4444-444444444444")
        };
        var evidence = new GradingEvidenceSnapshot(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "criterion-1",
            "Evidência",
            10m,
            null,
            "Texto",
            null,
            false,
            artifactIds);
        var snapshot = CreateSnapshot(evidence: [evidence]);
        var originalHash = snapshot.ContextHash;

        artifactIds.Clear();

        Assert.Equal(originalHash, snapshot.ContextHash);
        Assert.Single(snapshot.Evidence[0].ArtifactIds);
    }

    [Fact]
    public void Create_PreservaIdentidadeAssignmentECmidSemFallbackImplicito()
    {
        var snapshot = CreateSnapshot();

        Assert.Equal(42, snapshot.Assignment.CourseId);
        Assert.Equal(501, snapshot.Assignment.AssignmentId);
        Assert.Equal(9001, snapshot.Assignment.CourseModuleId);
    }

    [Fact]
    public void Create_RejeitaCoberturaInconsistente()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateSnapshot(
            coverage: new GradingEvidenceCoverage(
                TotalArtifacts: 1,
                IncludedArtifacts: 2,
                TotalChunks: 1,
                IncludedChunks: 1,
                SourceCharacterCount: 10,
                IncludedCharacterCount: 10,
                IsPartial: true)));

        Assert.Contains("cobertura", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GradingContextSnapshot CreateSnapshot(
        DateTimeOffset? publishedAt = null,
        int version = 1,
        string? teacherInstructions = "Valorize evidências.",
        IReadOnlyList<GradingCriterionSnapshot>? criteria = null,
        IReadOnlyList<string>? warnings = null,
        GradingEvidenceCoverage? coverage = null,
        IReadOnlyList<GradingEvidenceSnapshot>? evidence = null,
        string schemaVersion = GradingContextSnapshot.CurrentSchemaVersion) =>
        GradingContextSnapshot.Create(
            itemId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            batchId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            assignment: new MoodleAssignmentReference(42, 501, 9001),
            submission: new MoodleSubmissionReference(7001),
            student: new MoodleUserReference(3001),
            attemptNumber: 0,
            version: version,
            activityName: "Atividade de exemplo",
            assignmentStatement: "Descreva a solução.",
            criteria: criteria ??
            [
                new GradingCriterionSnapshot("criterion-1", "Argumentação", 10m, "rubric", "rubric-1")
            ],
            rubric: new GradingRubricSnapshot("Rubrica formal", "moodle:rubric:501"),
            gradingScale: new GradingScaleSnapshot(10m, "nota", "Escala de 0 a 10"),
            evidence: evidence ??
            [
                new GradingEvidenceSnapshot(
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    "criterion-1",
                    "A submissão apresenta uma justificativa.",
                    10m,
                    null,
                    "Justificativa encontrada no documento.",
                    null,
                    false,
                    [Guid.Parse("44444444-4444-4444-4444-444444444444")])
            ],
            artifacts:
            [
                new GradingArtifactReferenceSnapshot(
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    "submission_file",
                    "entrega.pdf",
                    "application/pdf",
                    "sha256:example",
                    1024,
                    "succeeded",
                    2,
                    false,
                    200,
                    180,
                    new GradingSourceMetadata("assign", 9001, 3, "Atividade", 0))
            ],
            extraction: new GradingExtractionSummary("succeeded", 2, false, 200, 180, null),
            coverage: coverage ?? new GradingEvidenceCoverage(1, 1, 2, 2, 200, 180, false),
            teacherInstructions: teacherInstructions,
            warnings: warnings,
            blockers: ["Nenhum bloqueador."],
            reviewRequired: false,
            publishedAt: publishedAt,
            schemaVersion: schemaVersion);
}
