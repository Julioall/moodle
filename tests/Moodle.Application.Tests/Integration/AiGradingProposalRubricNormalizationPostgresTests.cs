using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moodle.Application.Grading;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Integration;

[Trait("Category", "PostgresIntegration")]
public sealed class AiGradingProposalRubricNormalizationPostgresTests
{
    [Fact]
    public async Task NormalizationRoundTripsThroughPostgresItemAndProposalDocument()
    {
        var connectionString = Environment.GetEnvironmentVariable("MOODLE_POSTGRES_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "The PostgreSQL test gate must supply a connection.");
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var schema = new ConnectorDbContext(options))
        {
            await schema.ApplyVersionedSchemaAsync();
        }

        var batch = AssistedGradingBatch.Create(42, [501], $"rubric-normalization-{Guid.NewGuid():N}", 3001, 1);
        var item = AssistedGradingItem.Create(batch.Id, 42, 501, 7001, 3001, 0);
        var rubric = new GradingRubricSnapshot(
            "Rubrica sintetica",
            "moodle:core_grading_get_definitions",
            DefinitionId: 700,
            CourseModuleId: 9001,
            AreaName: "submissions",
            Method: "rubric",
            AssociationStatus: "active",
            DefinitionVersion: new string('a', 64),
            MinimumPoints: 0m,
            MaximumPoints: 15m,
            Criteria:
            [
                new("11", "Precisao", 0m, 5m,
                [new("101", 0m, "Parcial"), new("102", 5m, "Completa")]),
                new("12", "Analise", 0m, 10m,
                [new("201", 0m, "Ausente"), new("202", 7m, "Consistente"), new("203", 10m, "Avancada")])
            ]);
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
            new GradingScaleSnapshot(40m, "points", "Moodle", "numeric", 0m, "activity_settings", new string('c', 64)),
            evidence: [],
            artifacts: [],
            new GradingExtractionSummary("succeeded", 1, false, 20, 20, null),
            new GradingEvidenceCoverage(1, 1, 1, 1, 20, 20, false),
            teacherInstructions: null,
            warnings: [],
            blockers: [],
            reviewRequired: true);
        item.RecordContextSnapshot(snapshot);
        var input = new AiGradingProposalInput(
            1,
            snapshot.ContextHash,
            SuggestedGrade: 12m,
            "Feedback sintetico",
            [
                new("11", "Precisao", 5m, 5m, AiGradingCriterionSource.FormalRubric, "Evidencia sintetica", null, true, false, []),
                new("12", "Analise", 10m, 7m, AiGradingCriterionSource.FormalRubric, "Evidencia sintetica", null, true, false, [])
            ],
            [], [], [], .9m,
            new GradingExtractionSummary("succeeded", 1, false, 20, 20, null),
            new GradingEvidenceCoverage(1, 1, 1, 1, 20, 20, false));
        var proposal = AiGradingProposalFactory.Create(item, input, 40m, 1, contextSnapshot: snapshot);
        item.SetDraft(proposal.SuggestedGrade, proposal.Confidence, proposal.Feedback!, maxGrade: 40m);

        await using (var write = new ConnectorDbContext(options))
        {
            write.GradingBatches.Add(batch);
            write.GradingItems.Add(item);
            await write.SaveChangesAsync();
            await new GradingReviewRepository(write).PublishAsync(proposal, CancellationToken.None);
            await write.SaveChangesAsync();
        }

        await using var read = new ConnectorDbContext(options);
        var persistedItem = await read.GradingItems.AsNoTracking().SingleAsync(row => row.Id == item.Id);
        var persistedProposal = await new GradingReviewRepository(read)
            .GetLatestDocumentAsync(item.Id, CancellationToken.None);
        Assert.NotNull(persistedProposal);
        Assert.Equal(32m, persistedItem.SuggestedGrade);
        using var payload = JsonDocument.Parse(persistedProposal.PayloadJson);
        Assert.Equal(32m, payload.RootElement.GetProperty("SuggestedGrade").GetDecimal());
        var normalization = JsonSerializer.Deserialize<GradingScoreNormalizationSnapshot>(
            payload.RootElement.GetProperty("ScoreNormalization").GetRawText());
        Assert.NotNull(normalization);
        Assert.Equal(12m, normalization.RawRubricScore);
        Assert.Equal(15m, normalization.RubricMaximumPoints);
        Assert.Equal(40m, normalization.MoodleMaximumGrade);
        Assert.Equal(40m, normalization.NormalizationFactorNumerator);
        Assert.Equal(15m, normalization.NormalizationFactorDenominator);
        Assert.Equal(32m, normalization.NormalizedMoodleScore);
    }
}
