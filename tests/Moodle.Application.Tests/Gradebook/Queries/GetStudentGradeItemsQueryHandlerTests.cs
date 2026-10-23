using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Gradebook.Queries;

public sealed class GetStudentGradeItemsQueryHandlerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static GradebookItem MakeItem(string id, string name, string type, decimal? pct) =>
        new(Id: id, ItemName: name, ItemType: type, ItemModule: "assign",
            CategoryId: "1", GradeRaw: pct.HasValue ? pct.Value * 10 / 100 : null,
            GradeFormatted: pct?.ToString(), GradeMin: 0, GradeMax: 10,
            PercentageFormatted: pct, Feedback: null, FeedbackFormat: null,
            GradedDateSubmitted: null, GradedDateGraded: null, GraderId: null);

    private static GetStudentGradeItemsQueryHandler CreateHandler(
        IReadOnlyList<GradebookItem>? items = null)
    {
        var gateway = new FakeGradebookGateway(items ?? []);
        return new GetStudentGradeItemsQueryHandler(gateway);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ReturnsAllActivityItems_ExcludingCourseAndCategory()
    {
        var items = new[]
        {
            MakeItem("1", "SA1", "mod", 80m),
            MakeItem("2", "SA2", "mod", 55m),
            MakeItem("c", "Curso", "course", null),
            MakeItem("cat", "Categoria", "category", null)
        };
        var sut = CreateHandler(items);

        var result = await sut.Handle(
            new GetStudentGradeItemsQuery("10", "99", MinGradePercent: 60m),
            CancellationToken.None);

        Assert.Equal(2, result.Items.Count); // only mod items
        Assert.DoesNotContain(result.Items, i => i.ItemType == "course" || i.ItemType == "category");
    }

    [Fact]
    public async Task Handle_FlagsBelowMinimum_Correctly()
    {
        var items = new[]
        {
            MakeItem("1", "SA1", "mod", 80m), // above
            MakeItem("2", "SA2", "mod", 55m)  // below
        };
        var sut = CreateHandler(items);

        var result = await sut.Handle(
            new GetStudentGradeItemsQuery("10", "99", MinGradePercent: 60m),
            CancellationToken.None);

        Assert.Single(result.BelowMinimumItems);
        Assert.Equal("SA2", result.BelowMinimumItems[0].ItemName);
        Assert.True(result.BelowMinimumItems[0].BelowMinimum);
        Assert.False(result.Items.First(i => i.ItemName == "SA1").BelowMinimum);
    }

    [Fact]
    public async Task Handle_WhenNoItems_ReturnsWarning()
    {
        var sut = CreateHandler([]);

        var result = await sut.Handle(
            new GetStudentGradeItemsQuery("10", "99"),
            CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public async Task Handle_ItemWithNullPercentage_IsNotBelowMinimum()
    {
        var items = new[] { MakeItem("1", "SA1", "mod", null) };
        var sut = CreateHandler(items);

        var result = await sut.Handle(
            new GetStudentGradeItemsQuery("10", "99", MinGradePercent: 60m),
            CancellationToken.None);

        Assert.Single(result.Items);
        Assert.False(result.Items[0].BelowMinimum);
        Assert.Empty(result.BelowMinimumItems);
    }

    [Fact]
    public async Task Handle_DerivesPercentageForZeroGradeWhenMoodleOmitsIt()
    {
        var sut = CreateHandler([
            new GradebookItem("1", "SA1", "mod", "assign", "1",
                GradeRaw: 0m, GradeFormatted: "0", GradeMin: 0m, GradeMax: 10m,
                PercentageFormatted: null, Feedback: null, FeedbackFormat: null,
                GradedDateSubmitted: null, GradedDateGraded: null, GraderId: null)
        ]);

        var result = await sut.Handle(
            new GetStudentGradeItemsQuery("10", "99", MinGradePercent: 60m),
            CancellationToken.None);

        Assert.True(Assert.Single(result.Items).BelowMinimum);
        Assert.Equal(0m, result.Items[0].PercentageFormatted);
    }

    [Fact]
    public async Task Handle_MinGradePercentAtExactBoundary_IsNotBelowMinimum()
    {
        // exactly at 60% should not flag as below minimum
        var items = new[] { MakeItem("1", "SA1", "mod", 60m) };
        var sut = CreateHandler(items);

        var result = await sut.Handle(
            new GetStudentGradeItemsQuery("10", "99", MinGradePercent: 60m),
            CancellationToken.None);

        Assert.False(result.Items[0].BelowMinimum);
    }

    // ── Fake ─────────────────────────────────────────────────────────────────

    private sealed class FakeGradebookGateway(IReadOnlyList<GradebookItem> items)
        : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId, string studentId, CancellationToken cancellationToken) =>
            Task.FromResult(new CourseGradebook(courseId, studentId, items));
    }
}
