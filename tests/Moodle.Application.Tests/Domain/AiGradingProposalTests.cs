using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Domain;

public sealed class AiGradingProposalTests
{
    [Fact]
    public void Create_GeraHashDeterministicoEExigeRevisaoQuandoConfiancaBaixa()
    {
        var criteria = new[]
        {
            new AiGradingCriterionProposal(
                "C1",
                "Aplicacao do conceito",
                10m,
                8m,
                AiGradingCriterionSource.FormalRubric,
                "artifact evidence",
                null,
                TeacherReviewRequired: false,
                TeacherApproved: false,
                ArtifactIds: [])
        };
        var coverage = CompleteCoverage();
        var extraction = CompleteExtraction();
        var scale = new GradingScaleSnapshot(10m, "points", "moodle");
        var confidence = AiGradingConfidenceCalculator.Calculate(
            0.9m,
            coverage,
            extraction,
            scale,
            criteria);

        var first = AiGradingProposal.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "context-hash",
            8m,
            "Feedback",
            criteria,
            [],
            [],
            scale,
            extraction,
            coverage,
            confidence,
            reviewRequired: false,
            createdAt: DateTimeOffset.UtcNow);
        var second = AiGradingProposal.Create(
            first.ItemId,
            first.BatchId,
            first.Version,
            first.ContextHash,
            first.SuggestedGrade,
            first.Feedback,
            first.Criteria,
            first.Evidence,
            first.Gaps,
            first.GradingScale,
            first.Extraction,
            first.Coverage,
            confidence,
            reviewRequired: false,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(first.ProposalHash, second.ProposalHash);
        Assert.Equal(0.9m, first.Confidence);
        Assert.False(first.ReviewRequired);
    }

    [Theory]
    [InlineData("assignment_context", AiGradingCriterionSource.StatementDerived)]
    [InlineData("teacher_instruction", AiGradingCriterionSource.TeacherDefined)]
    [InlineData("model", AiGradingCriterionSource.Unknown)]
    public void Create_NormalizaFontesDeCriterioUsadasPeloModelo(string source, string expected)
    {
        var proposal = AiGradingProposal.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "context-hash",
            0m,
            "A entrega nao corresponde a atividade solicitada.",
            [new AiGradingCriterionProposal(
                "C1",
                "Compatibilidade com o enunciado",
                10m,
                null,
                source,
                "Arquivo analisado",
                "Revisar a atividade correta",
                TeacherReviewRequired: true,
                TeacherApproved: false,
                ArtifactIds: [])],
            [],
            [],
            new GradingScaleSnapshot(10m, "points", "moodle"),
            CompleteExtraction(),
            CompleteCoverage(),
            new AiGradingConfidenceResult(.2m, ["delivery_mismatch"], true),
            reviewRequired: true);

        Assert.Equal(expected, Assert.Single(proposal.Criteria).Source);
    }

    [Fact]
    public void Create_NaoAceitaNotaSemEscalaConfirmada()
    {
        Assert.Throws<InvalidOperationException>(() => AiGradingProposal.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            null,
            5m,
            "Feedback",
            [],
            [],
            [],
            gradingScale: null,
            CompleteExtraction(),
            CompleteCoverage(),
            new AiGradingConfidenceResult(0m, ["grading_scale_unconfirmed"], true),
            reviewRequired: true));
    }

    [Fact]
    public void Create_NaoPermiteCriterioGeradoDistribuirPontosSemAprovacao()
    {
        var criterion = new AiGradingCriterionProposal(
            "G1",
            "Criterio auxiliar",
            10m,
            4m,
            AiGradingCriterionSource.GeneratedSupport,
            null,
            null,
            TeacherReviewRequired: true,
            TeacherApproved: false,
            ArtifactIds: []);

        Assert.Throws<InvalidOperationException>(() => AiGradingProposal.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            null,
            null,
            "Feedback",
            [criterion],
            [],
            [],
            null,
            CompleteExtraction(),
            CompleteCoverage(),
            new AiGradingConfidenceResult(0.2m, ["generated_criteria_not_approved"], true),
            reviewRequired: true));
    }

    [Fact]
    public void FromLegacy_NaoPromoveNotaNemConfianca()
    {
        var proposal = AiGradingProposal.FromLegacy(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "context-hash",
            8m,
            "Feedback legado");

        Assert.Null(proposal.SuggestedGrade);
        Assert.Equal(0m, proposal.Confidence);
        Assert.True(proposal.ReviewRequired);
        Assert.Equal("legacy_review_required", proposal.Status);
        Assert.Contains("legacy_proposal_without_evidence", proposal.UncertaintyReasons);
    }

    [Fact]
    public void Create_IncluiHashDaSubmissaoNaIdentidadeCanonica()
    {
        var common = new
        {
            ItemId = Guid.NewGuid(),
            BatchId = Guid.NewGuid(),
            Scale = new GradingScaleSnapshot(10m, "points", "moodle"),
            Extraction = CompleteExtraction(),
            Coverage = CompleteCoverage(),
            Confidence = new AiGradingConfidenceResult(.9m, [], true)
        };

        var first = AiGradingProposal.Create(
            common.ItemId, common.BatchId, 1, "context", 8m, "Feedback", [], [], [], common.Scale,
            common.Extraction, common.Coverage, common.Confidence, true,
            submissionContentHash: new string('a', 64));
        var changed = AiGradingProposal.Create(
            common.ItemId, common.BatchId, 1, "context", 8m, "Feedback", [], [], [], common.Scale,
            common.Extraction, common.Coverage, common.Confidence, true,
            submissionContentHash: new string('b', 64));

        Assert.Equal(new string('a', 64), first.SubmissionContentHash);
        Assert.NotEqual(first.ProposalHash, changed.ProposalHash);
        Assert.Throws<ArgumentException>(() => AiGradingProposal.Create(
            common.ItemId, common.BatchId, 1, "context", 8m, "Feedback", [], [], [], common.Scale,
            common.Extraction, common.Coverage, common.Confidence, true,
            submissionContentHash: "not-a-hash"));
    }

    [Fact]
    public void ConfidenceDiminuiComCoberturaParcialEEscalaDesconhecida()
    {
        var full = AiGradingConfidenceCalculator.Calculate(
            0.9m,
            CompleteCoverage(),
            CompleteExtraction(),
            new GradingScaleSnapshot(10m, "points", "moodle"),
            [new AiGradingCriterionProposal("C1", "Criterio", 10m, 8m, AiGradingCriterionSource.FormalRubric, null, null, false, false, [])]);
        var partial = AiGradingConfidenceCalculator.Calculate(
            0.9m,
            new GradingEvidenceCoverage(2, 1, 2, 1, 100, 25, true),
            new GradingExtractionSummary("partial", 2, true, 100, 25, "limit"),
            null,
            [new AiGradingCriterionProposal("C1", "Criterio", null, null, AiGradingCriterionSource.StatementDerived, null, "gap", true, false, [])]);

        Assert.True(full.Confidence > partial.Confidence);
        Assert.True(partial.ReviewRequired);
        Assert.Contains("grading_scale_unconfirmed", partial.UncertaintyReasons);
        Assert.Contains("evidence_coverage_partial", partial.UncertaintyReasons);
    }

    private static GradingEvidenceCoverage CompleteCoverage() =>
        new(1, 1, 1, 1, 100, 100, false);

    private static GradingExtractionSummary CompleteExtraction() =>
        new("succeeded", 1, false, 100, 100, null);
}
