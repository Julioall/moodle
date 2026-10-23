using Moodle.Presentation;

public sealed class AppPendingContractTests
{
    [Fact]
    public void Pending_item_preserves_compound_identity_and_objective_reasons()
    {
        var item = new AppPendingDto(
            "senai-goias", "course-1", "student-1", "activity-1", "Ana", "Atividade", "awaiting_grading", "risk",
            new[] { "Entrega submetida em 2026-08-09T10:00:00Z" }, null, null, null, null, "https://moodle.example/activity/1");

        Assert.Equal("senai-goias", item.ConnectionRef);
        Assert.Equal("student-1", item.StudentId);
        Assert.NotEmpty(item.Factors);
        Assert.False(item.CanGrade);
        Assert.False(item.CanWrite);
    }

    [Fact]
    public void Contract_mapper_creates_access_and_submission_items_without_mutation_capabilities()
    {
        var generatedAt = new DateTimeOffset(2026, 8, 10, 3, 0, 0, TimeSpan.Zero);
        var items = AppPendingContractMapper.Build(
            "senai-goias",
            "course-1",
            new[]
            {
                new AppPendingSourceRow("student-1", "Ana", null, "activity-1", "Entrega", "pending_submission", DateTimeOffset.UtcNow.AddDays(2), false, false)
            },
            new[]
            {
                new AppPendingAccessRow("student-2", "Bruno", DateTimeOffset.UtcNow.AddDays(-45))
            },
            generatedAt);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Type == "pending_submission" && item.ActivityId == "activity-1");
        Assert.Contains(items, item => item.Type == "no_recent_access" && item.StudentId == "student-2");
        Assert.All(items, item =>
        {
            Assert.Equal("senai-goias", item.ConnectionRef);
            Assert.Equal("course-1", item.CourseId);
            Assert.False(item.CanGrade);
            Assert.False(item.CanWrite);
            Assert.NotEmpty(item.Factors);
        });
    }
}
