using Moodle.Application.Abstractions;
using Moodle.Application.Completion.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Completion;

public sealed class GetStudentsWithoutRecentAccessQueryHandlerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CourseParticipantSummary MakeStudent(string id, string name,
        DateTimeOffset? lastAccess) =>
        new(UserId: id, FullName: name, Email: null, Suspended: false,
            FirstAccessAt: null, LastAccessAt: null, LastCourseAccessAt: lastAccess,
            Roles: [], Groups: []);

    private static GetStudentsWithoutRecentAccessQueryHandler CreateHandler(
        IReadOnlyList<CourseParticipantSummary> students,
        bool hasMore = false)
    {
        var participants = new FakeParticipantsGateway(students, hasMore);
        var currentUser = new FakeCurrentUserGateway();
        return new GetStudentsWithoutRecentAccessQueryHandler(participants, currentUser);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ReturnsStudentsAboveThreshold_Only()
    {
        var students = new[]
        {
            MakeStudent("1", "Alice", Now.AddDays(-10)),  // 10 days ago → inactive
            MakeStudent("2", "Bob",   Now.AddDays(-3)),   // 3 days ago → active
            MakeStudent("3", "Carol", Now.AddDays(-8))    // 8 days ago → inactive
        };
        var sut = CreateHandler(students);

        var result = await sut.Handle(
            new GetStudentsWithoutRecentAccessQuery("10", DaysWithoutAccess: 7),
            CancellationToken.None);

        Assert.Equal(2, result.Students.Count);
        Assert.Contains(result.Students, s => s.StudentId == "1");
        Assert.Contains(result.Students, s => s.StudentId == "3");
        Assert.DoesNotContain(result.Students, s => s.StudentId == "2");
    }

    [Fact]
    public async Task Handle_NeverAccessedStudents_AreAlwaysIncluded()
    {
        var students = new[]
        {
            MakeStudent("1", "NeverAccessed", null),
            MakeStudent("2", "RecentAccess", Now.AddDays(-1))
        };
        var sut = CreateHandler(students);

        var result = await sut.Handle(
            new GetStudentsWithoutRecentAccessQuery("10", DaysWithoutAccess: 7),
            CancellationToken.None);

        Assert.Single(result.Students);
        Assert.Equal("1", result.Students[0].StudentId);
        Assert.True(result.Students[0].NeverAccessed);
    }

    [Fact]
    public async Task Handle_SuggestedRecipientIds_MatchInactiveStudents()
    {
        var students = new[]
        {
            MakeStudent("1", "Inactive", Now.AddDays(-14)),
            MakeStudent("2", "Active",   Now.AddDays(-2))
        };
        var sut = CreateHandler(students);

        var result = await sut.Handle(
            new GetStudentsWithoutRecentAccessQuery("10", DaysWithoutAccess: 7),
            CancellationToken.None);

        Assert.Equal(result.Students.Select(s => s.StudentId), result.SuggestedRecipientIds);
    }

    [Fact]
    public async Task Handle_EmptyCourse_ReturnsTotalZero()
    {
        var sut = CreateHandler([]);

        var result = await sut.Handle(
            new GetStudentsWithoutRecentAccessQuery("10"),
            CancellationToken.None);

        Assert.Equal(0, result.TotalStudentsAnalyzed);
        Assert.Empty(result.Students);
    }

    [Fact]
    public async Task Handle_StudentsOrderedByInactivityDescending()
    {
        var students = new[]
        {
            MakeStudent("1", "Bob",   Now.AddDays(-8)),
            MakeStudent("2", "Alice", Now.AddDays(-15))
        };
        var sut = CreateHandler(students);

        var result = await sut.Handle(
            new GetStudentsWithoutRecentAccessQuery("10", DaysWithoutAccess: 7),
            CancellationToken.None);

        // Alice (15 days) should come before Bob (8 days)
        Assert.Equal("2", result.Students[0].StudentId);
        Assert.Equal("1", result.Students[1].StudentId);
    }

    [Fact]
    public async Task Handle_DeclaresPartialAnalysisWhenParticipantPageHasMore()
    {
        var sut = CreateHandler(
            [MakeStudent("1", "Aluno", Now.AddDays(-10))],
            hasMore: true);

        var result = await sut.Handle(
            new GetStudentsWithoutRecentAccessQuery("10", DaysWithoutAccess: 7, MaxStudentsToAnalyze: 1),
            CancellationToken.None);

        Assert.NotNull(result.Warning);
        Assert.Contains("parcial", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MaxStudentsToAnalyze", result.Warning, StringComparison.Ordinal);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeParticipantsGateway(
        IReadOnlyList<CourseParticipantSummary> students,
        bool hasMore)
        : IMoodleParticipantsGateway
    {
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");
        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
            string userExternalId, string courseId, ParticipantStatusFilter statusFilter,
            int page, int pageSize, bool studentsOnly, bool includeEmail, string? groupId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CourseParticipantsPage(courseId, page, pageSize,
                statusFilter, studentsOnly, includeEmail, HasMore: hasMore, students));

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);
    }

    private sealed class FakeCurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult(42L);
    }
}
