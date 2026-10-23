using Microsoft.EntityFrameworkCore;
using Moodle.Domain.Grading;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class GradingReviewReadStoreTests
{
    [Fact]
    public async Task GetPageAsync_returns_local_context_names_and_coverage_without_moodle()
    {
        await using var db = new ConnectorDbContext(new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase($"grading-read-model-{Guid.NewGuid():N}")
            .Options);
        var batch = AssistedGradingBatch.Create(10, [501], "teacher-1", 321, 1, courseDisplayName: "Curso de Redes");
        var item = AssistedGradingItem.Create(batch.Id, 10, 501, 9001, 101, 1, "Ana Silva");
        item.SetDraft(8m, 0.9m, "Rascunho");
        var context = GradingContextSnapshot.Create(
            item.Id,
            batch.Id,
            new MoodleAssignmentReference(10, 501, 77),
            new MoodleSubmissionReference(9001),
            new MoodleUserReference(101),
            1,
            1,
            "Tarefa de Redes",
            "Explique o protocolo TCP.",
            [],
            null,
            new GradingScaleSnapshot(10m, null, null, "unknown"),
            [],
            [],
            new GradingExtractionSummary("succeeded", 0, false, 0, 0, null),
            new GradingEvidenceCoverage(0, 0, 0, 0, 0, 0, false),
            null,
            [],
            [],
            reviewRequired: false);

        db.GradingBatches.Add(batch);
        db.GradingItems.Add(item);
        db.GradingContextSnapshots.Add(GradingContextSnapshotDocument.FromSnapshot(context));
        db.GradingEvidence.Add(new GradingEvidence(Guid.NewGuid(), item.Id, "c1", "TCP", 10m, 8m, "Evidencia", null, false, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var page = await new GradingReviewReadStore(db).GetPageAsync(batch.Id, 1, 50, CancellationToken.None);

        Assert.Equal("Curso de Redes", page.CourseName);
        Assert.Equal("local_read_model", page.DataSource);
        Assert.Equal(3, page.QueryCount);
        var row = Assert.Single(page.Items);
        Assert.Equal("Ana Silva", row.StudentName);
        Assert.Equal("Tarefa de Redes", row.AssignmentName);
        Assert.Equal("unknown", row.GradingMode);
        Assert.Equal(10m, row.MaxGrade);
        Assert.NotNull(row.Coverage);
        Assert.Single(row.Evidence);
    }
}
