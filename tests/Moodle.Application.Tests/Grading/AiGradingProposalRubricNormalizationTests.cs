using Moodle.Application.Grading;
using Moodle.Domain.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class AiGradingProposalRubricNormalizationTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(32)]
    public void FactoryConverte12De15Para32De40SemAplicarDuasVezes(int suppliedGrade)
    {
        var (item, snapshot) = CreateContextSnapshot();
        var proposal = AiGradingProposalFactory.Create(
            item,
            CreateInput(snapshot.ContextHash, suppliedGrade),
            authoritativeMaxGrade: 40m,
            version: 1,
            contextSnapshot: snapshot);

        Assert.Equal(32m, proposal.SuggestedGrade);
        Assert.Equal(12m, proposal.ScoreNormalization!.RawRubricScore);
        Assert.Equal(15m, proposal.ScoreNormalization.RubricMaximumPoints);
        Assert.Equal(40m, proposal.ScoreNormalization.MoodleMaximumGrade);
        Assert.Equal(40m, proposal.ScoreNormalization.NormalizationFactorNumerator);
        Assert.Equal(15m, proposal.ScoreNormalization.NormalizationFactorDenominator);
        Assert.Equal(40m / 15m, proposal.ScoreNormalization.NormalizationFactor);
        Assert.True(proposal.ScoreNormalization.ScaleMismatch);
        Assert.Equal("moodle-rubric-linear-zero-origin-v1-4dp-away-from-zero", proposal.ScoreNormalization.RuleVersion);
        Assert.Equal(proposal.ScoreNormalization, ReadScoreNormalization(AiGradingProposal.SerializeCanonicalPayload(proposal)));
    }

    [Fact]
    public void FactoryRejeitaPontosDeRubricaSemAssociacaoOficial()
    {
        var (item, snapshot) = CreateContextSnapshot(
            rubric: CreateRubric() with { AssociationStatus = "unverified" });

        var error = Assert.Throws<InvalidOperationException>(() => AiGradingProposalFactory.Create(
            item,
            CreateInput(snapshot.ContextHash, 12m),
            authoritativeMaxGrade: 40m,
            version: 1,
            contextSnapshot: snapshot));

        Assert.Contains("associacao ativa verificavel", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1", "15", "rubric-origin", "origem zero")]
    [InlineData("0", "", "numeric-scale", "exige escala numerica")]
    public void FactoryBloqueiaFaixaNaoCompatível(
        string rubricMinimumText,
        string maximumGradeText,
        string mode,
        string expectedMessage)
    {
        var rubricMinimum = decimal.Parse(rubricMinimumText, System.Globalization.CultureInfo.InvariantCulture);
        decimal? maximumGrade = string.IsNullOrEmpty(maximumGradeText)
            ? null
            : decimal.Parse(maximumGradeText, System.Globalization.CultureInfo.InvariantCulture);
        var scale = maximumGrade is null
            ? new GradingScaleSnapshot(null, "scale", "Moodle", "scale", null)
            : new GradingScaleSnapshot(maximumGrade, "points", "Moodle", "numeric", 0m);
        var rubric = mode == "rubric-origin"
            ? CreateRubric(minimumPoints: rubricMinimum)
            : CreateRubric();
        var (item, snapshot) = CreateContextSnapshot(rubric: rubric, scale: scale);

        var error = Assert.Throws<InvalidOperationException>(() => AiGradingProposalFactory.Create(
            item,
            CreateInput(snapshot.ContextHash, 12m),
            authoritativeMaxGrade: maximumGrade,
            version: 1,
            contextSnapshot: snapshot));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryArredondaSomenteUmaVezNaPrecisaoPersistida()
    {
        var rubric = new GradingRubricSnapshot(
            "Rubrica sintetica",
            "moodle:core_grading_get_definitions",
            DefinitionId: 701,
            CourseModuleId: 9002,
            AreaName: "submissions",
            Method: "rubric",
            AssociationStatus: "active",
            DefinitionVersion: new string('b', 64),
            MinimumPoints: 0m,
            MaximumPoints: 200000m,
            Criteria:
            [
                new("31", "Precisao", 0m, 200000m,
                [
                    new("310", 0m, "Insuficiente"),
                    new("311", 1m, "Parcial"),
                    new("312", 200000m, "Completa"),
                    new("313", 10m, "Empate")
                ])
            ]);
        var (item, snapshot) = CreateContextSnapshot(
            rubric,
            new GradingScaleSnapshot(1m, "points", "Moodle", "numeric", 0m));
        var input = new AiGradingProposalInput(
            1,
            snapshot.ContextHash,
            SuggestedGrade: 10m,
            "Feedback sintetico",
            [new AiGradingCriterionInput("31", "Precisao", 200000m, 10m, AiGradingCriterionSource.FormalRubric, "Evidencia sintetica", null, true, false, [])],
            [], [], [], .9m,
            new GradingExtractionSummary("succeeded", 1, false, 10, 10, null),
            new GradingEvidenceCoverage(1, 1, 1, 1, 10, 10, false));

        var proposal = AiGradingProposalFactory.Create(item, input, 1m, 1, contextSnapshot: snapshot);

        Assert.Equal(0.00005m, proposal.ScoreNormalization!.UnroundedMoodleScore);
        Assert.Equal(0.0001m, proposal.ScoreNormalization.NormalizedMoodleScore);
        Assert.Equal(0.0001m, proposal.SuggestedGrade);
    }

    [Fact]
    public void FactoryRejeitaPontosIntermediariosNaoExistentesNosNiveisMoodle()
    {
        var (item, snapshot) = CreateContextSnapshot();
        var input = CreateInput(snapshot.ContextHash, 12m) with
        {
            Criteria = CreateInput(snapshot.ContextHash, 12m).Criteria!
                .Select(criterion => criterion.CriterionId == "12"
                    ? criterion with { SuggestedPoints = 7.5m }
                    : criterion)
                .ToArray()
        };

        Assert.Throws<InvalidOperationException>(() => AiGradingProposalFactory.Create(
            item,
            input,
            authoritativeMaxGrade: 40m,
            version: 1,
            contextSnapshot: snapshot));
    }

    [Fact]
    public void FactoryRejeitaNotaInformadaForaDaFaixaRubricaOuMoodle()
    {
        var (item, snapshot) = CreateContextSnapshot();

        var error = Assert.Throws<InvalidOperationException>(() => AiGradingProposalFactory.Create(
            item,
            CreateInput(snapshot.ContextHash, 41m),
            authoritativeMaxGrade: 40m,
            version: 1,
            contextSnapshot: snapshot));

        Assert.Contains("diverge", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactoryBloqueiaNotaNormalizadaForaDaPrecisaoPersistida()
    {
        var (item, snapshot) = CreateContextSnapshot(
            scale: new GradingScaleSnapshot(1_000_000m, "points", "Moodle", "numeric", 0m));
        var input = CreateInput(snapshot.ContextHash, 15m) with
        {
            Criteria = CreateInput(snapshot.ContextHash, 15m).Criteria!
                .Select(criterion => criterion.CriterionId == "12"
                    ? criterion with { SuggestedPoints = 10m }
                    : criterion)
                .ToArray()
        };

        var error = Assert.Throws<InvalidOperationException>(() => AiGradingProposalFactory.Create(
            item,
            input,
            authoritativeMaxGrade: 1_000_000m,
            version: 1,
            contextSnapshot: snapshot));

        Assert.Contains("precisao persistida", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AiGradingProposalInput CreateInput(string contextHash, decimal? suggestedGrade) => new(
        Version: 1,
        ContextHash: contextHash,
        SuggestedGrade: suggestedGrade,
        Feedback: "Feedback sintetico",
        Criteria:
        [
            new("11", "Precisao", 5m, 5m, AiGradingCriterionSource.FormalRubric, "Evidencia sintetica", null, true, false, []),
            new("12", "Analise", 10m, 7m, AiGradingCriterionSource.FormalRubric, "Evidencia sintetica", null, true, false, [])
        ],
        Evidence: [],
        ResourceUris: [],
        Gaps: [],
        ModelConfidence: .9m,
        Extraction: new GradingExtractionSummary("succeeded", 1, false, 20, 20, null),
        Coverage: new GradingEvidenceCoverage(1, 1, 1, 1, 20, 20, false));

    private static GradingRubricSnapshot CreateRubric(decimal minimumPoints = 0m) => new(
        "Rubrica sintetica",
        "moodle:core_grading_get_definitions",
        DefinitionId: 700,
        CourseModuleId: 9001,
        AreaName: "submissions",
        Method: "rubric",
        AssociationStatus: "active",
        DefinitionVersion: new string('a', 64),
        MinimumPoints: minimumPoints,
        MaximumPoints: 15m,
        Criteria:
        [
            new("11", "Precisao", minimumPoints, 5m,
            [
                new("101", minimumPoints, "Parcial"),
                new("102", 5m, "Completa")
            ]),
            new("12", "Analise", 0m, 10m,
            [
                new("201", 0m, "Ausente"),
                new("202", 7m, "Consistente"),
                new("203", 10m, "Avancada")
            ])
        ]);

    private static (AssistedGradingItem Item, GradingContextSnapshot Snapshot) CreateContextSnapshot(
        GradingRubricSnapshot? rubric = null,
        GradingScaleSnapshot? scale = null)
    {
        var batch = AssistedGradingBatch.Create(42, [501], "synthetic-teacher", 3001, 1);
        var item = AssistedGradingItem.Create(batch.Id, 42, 501, 7001, 3001, 0);
        rubric ??= CreateRubric();
        scale ??= new GradingScaleSnapshot(40m, "points", "Moodle", "numeric", 0m, "activity_settings", new string('c', 64), 0);
        var snapshot = GradingContextSnapshot.Create(
            item.Id,
            batch.Id,
            new MoodleAssignmentReference(42, 501, 9001),
            new MoodleSubmissionReference(7001),
            new MoodleUserReference(3001),
            0,
            1,
            "Tarefa sintetica",
            "Enunciado sintetico",
            criteria: [],
            rubric,
            scale,
            evidence: [],
            artifacts: [],
            new GradingExtractionSummary("succeeded", 1, false, 20, 20, null),
            new GradingEvidenceCoverage(1, 1, 1, 1, 20, 20, false),
            teacherInstructions: null,
            warnings: [],
            blockers: [],
            reviewRequired: true);
        item.RecordContextSnapshot(snapshot);
        return (item, snapshot);
    }

    private static GradingScoreNormalizationSnapshot? ReadScoreNormalization(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return System.Text.Json.JsonSerializer.Deserialize<GradingScoreNormalizationSnapshot>(
            document.RootElement.GetProperty("ScoreNormalization").GetRawText());
    }
}
