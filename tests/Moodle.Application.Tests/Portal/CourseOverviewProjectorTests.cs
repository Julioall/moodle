using Moodle.Application.Abstractions;
using Moodle.Application.Portal.CourseOverview;
using Moodle.Domain;

namespace Moodle.Application.Tests.Portal;

public sealed class CourseOverviewProjectorTests
{
    [Fact]
    public void Missing_students_snapshot_has_no_observed_count()
    {
        var result = Project(students: null, gradebook: null);

        Assert.Null(result.Summary.ObservedStudentCount);
        Assert.Equal("unavailable", result.Summary.StudentCountState);
    }

    [Fact]
    public void Partial_snapshots_do_not_become_zero_and_null_access_stays_unknown()
    {
        var students = Students(
            [Student("10", "Ana", null)],
            complete: false,
            hasMore: true);

        var result = Project(students, gradebook: null);

        Assert.Equal(1, result.Summary.ObservedStudentCount);
        Assert.Null(result.Summary.TotalStudentCount);
        Assert.Null(result.Summary.AveragePercentage);
        Assert.Equal("partial", result.Summary.StudentCountState);
        Assert.Equal("unavailable", result.Summary.AverageState);
        Assert.Equal("unknown", Assert.Single(result.Students).LastAccessState);
        Assert.Null(result.Students[0].LastCourseAccessAt);
        Assert.Equal("partial", result.Coverage["students"].State);
        Assert.Equal("unavailable", result.Coverage["gradebook"].State);
    }

    [Fact]
    public void Legacy_roster_without_classification_evidence_is_not_a_complete_population()
    {
        var snapshot = new MoodleSnapshotEnvelope<CourseParticipantsPage>(
            new CourseParticipantsPage("42", 1, 500, ParticipantStatusFilter.Active, true, false, false, [Student("10", "Ana", null)]),
            DateTimeOffset.UtcNow, false, false, "hot", IsComplete: true);

        var result = Project(snapshot, null);

        Assert.Null(result.Summary.TotalStudentCount);
        Assert.Equal("partial", result.Summary.StudentCountState);
        Assert.Contains("legacy_coverage_unverified", result.Coverage["students"].Reasons);
    }

    [Fact]
    public void Course_total_average_uses_only_covered_percentages()
    {
        var students = Students([Student("10", "Ana", DateTimeOffset.UtcNow), Student("11", "Bruno", null)]);
        var gradebook = Gradebook(
            [new("course-total", "Total", "course", "", null, null, null, 0, 20)],
            [
                new("10", "course-total", 10, "10", 50, null, null, null, null, null),
                new("11", "course-total", 20, "20", 100, null, null, null, null, null),
            ],
            complete: true);

        var result = Project(students, gradebook);

        Assert.Equal(75m, result.Summary.AveragePercentage);
        Assert.Equal(2, result.Summary.AverageDenominator);
        Assert.Equal("complete", result.Summary.AverageState);
        Assert.All(result.Students, student => Assert.NotNull(student.Grade?.Percentage));
    }

    [Fact]
    public void Course_total_average_ignores_gradebook_rows_outside_the_active_roster()
    {
        var students = Students([Student("10", "Ana", null)]);
        var gradebook = Gradebook(
            [new("course-total", "Total", "course", "", null, null, null, 0, 10)],
            [
                new("10", "course-total", 8, "8", 80, null, null, null, null, null),
                new("former-student", "course-total", 0, "0", 0, null, null, null, null, null),
            ],
            complete: true);

        var result = Project(students, gradebook);

        Assert.Equal(80m, result.Summary.AveragePercentage);
        Assert.Equal(1, result.Summary.KnownGradeCount);
        Assert.Equal(1, result.Summary.AverageDenominator);
        Assert.Equal("partial", result.Summary.AverageState);
        Assert.DoesNotContain(result.Students, student => student.StudentId == "former-student");
    }

    [Fact]
    public void Student_without_a_course_grade_is_not_reported_as_zero()
    {
        var students = Students([Student("10", "Ana", null)]);
        var gradebook = Gradebook(
            [new("course-total", "Total", "course", "", null, null, null, 0, 10)],
            [new("10", "course-total", null, null, null, null, null, null, null, null)],
            complete: true);

        var result = Project(students, gradebook);
        var grade = Assert.Single(result.Students).Grade;

        Assert.Null(grade?.Value);
        Assert.Equal(GradebookCoverageStates.NoGrade, grade?.State);
        Assert.Null(result.Summary.AveragePercentage);
        Assert.Equal("unavailable", result.Summary.AverageState);
    }

    [Fact]
    public void Raw_course_grades_without_max_are_averaged_on_the_original_scale()
    {
        var students = Students([Student("10", "Ana", null), Student("11", "Bruno", null)]);
        var legacyGradebook = new CourseGradebookSnapshot(
            "42",
            new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase)
            {
                ["10"] = new("42", "10", [new GradebookItem(
                    "course-total", "Total", "course", "", null, 8m, "8", 0m, null,
                    null, null, null, null, null, null)]),
                ["11"] = new("42", "11", [new GradebookItem(
                    "course-total", "Total", "course", "", null, 6m, "6", 0m, null,
                    null, null, null, null, null, null)]),
            },
            new GradebookSnapshotCoverage("legacy", 2, 2, true, false, [], []))
            .WithCanonicalProjection();
        var gradebook = new MoodleSnapshotEnvelope<CourseGradebookSnapshot>(
            legacyGradebook, DateTimeOffset.UtcNow, false, false, "hot", IsComplete: true);

        var result = Project(students, gradebook);

        var row = Assert.Single(result.Students, student => student.StudentId == "10").Grade;
        Assert.Equal(8m, row?.Value);
        Assert.Null(row?.Max);
        Assert.Null(row?.Percentage);
        Assert.Equal(GradebookCoverageStates.Covered, row?.State);
        Assert.Null(result.Summary.AveragePercentage);
        Assert.Equal(2, result.Summary.KnownGradeCount);
        Assert.Equal(7m, result.Summary.AverageGradeValue);
        Assert.Equal(2, result.Summary.AverageDenominator);
        Assert.Equal("complete", result.Summary.AverageState);
        Assert.Equal("complete", result.Coverage["gradebook"].State);
    }

    [Fact]
    public void Moodle_percentage_is_used_when_item_maximum_is_missing()
    {
        var students = Students([Student("10", "Ana", null)]);
        var gradebook = Gradebook(
            [new("course-total", "Total", "course", "", null, null, null, 0, null)],
            [new("10", "course-total", 8, "8", 64, null, null, null, null, null)],
            complete: true);

        var result = Project(students, gradebook);

        Assert.Equal(64m, Assert.Single(result.Students).Grade?.Percentage);
        Assert.Equal(64m, result.Summary.AveragePercentage);
        Assert.Null(result.Summary.AverageGradeValue);
        Assert.Equal("complete", result.Summary.AverageState);
    }

    [Fact]
    public void Descending_sort_changes_names_and_keeps_stable_id_ties()
    {
        var students = Students([Student("10", "Ana", null), Student("12", "Bruno", null), Student("11", "Bruno", null)]);

        var result = Project(students, null, new CourseOverviewRequest("", "name_desc", 1, 10));

        Assert.Equal(["Bruno", "Bruno", "Ana"], result.Students.Select(student => student.FullName));
        Assert.Equal(["11", "12", "10"], result.Students.Select(student => student.StudentId));
    }

    [Fact]
    public void Ambiguous_course_total_is_not_selected_arbitrarily()
    {
        var students = Students([Student("10", "Ana", null)]);
        var gradebook = Gradebook(
            [
                new("total-a", "Total A", "course", "", null, null, null, 0, 10),
                new("total-b", "Total B", "course", "", null, null, null, 0, 100),
            ],
            [new("10", "total-a", 8, "8", 80, null, null, null, null, null)],
            complete: true);

        var result = Project(students, gradebook);

        Assert.Null(Assert.Single(result.Students).Grade?.Value);
        Assert.Equal("ambiguous_course_total", result.Students[0].Grade?.Reason);
        Assert.Null(result.Summary.AveragePercentage);
    }

    [Fact]
    public void Snapshot_skew_marks_average_partial_without_discarding_known_grade()
    {
        var students = Students([Student("10", "Ana", null)]);
        var gradebook = Gradebook(
            [new("course-total", "Total", "course", "", null, null, null, 0, 10)],
            [new("10", "course-total", 8, "8", 80, null, null, null, null, null)],
            complete: true);

        var result = CourseOverviewProjector.Project(
            new CourseSummary("42", null, null, "Lógica", "Lógica", null, null, null, null, true, null, null, null, null, null, null),
            "demo", null, students, gradebook,
            new CourseOverviewRequest("", "name_asc", 1, 10),
            true, true, false, 901, DateTimeOffset.UtcNow);

        Assert.Equal(80m, result.Summary.AveragePercentage);
        Assert.Equal("partial", result.Summary.AverageState);
        Assert.Equal("partial", result.Coverage["gradebook"].State);
        Assert.Contains("snapshot_skew", result.Coverage["gradebook"].Reasons);
    }

    private static CourseOverviewResponse Project(
        MoodleSnapshotEnvelope<CourseParticipantsPage>? students,
        MoodleSnapshotEnvelope<CourseGradebookSnapshot>? gradebook,
        CourseOverviewRequest? request = null) =>
        CourseOverviewProjector.Project(
            new CourseSummary("42", null, "LOG", "Lógica Computacional", "Lógica Computacional", 1, "TDS", null, null, true, null, null, null, null, null, null),
            "demo",
            null,
            students,
            gradebook,
            request ?? new CourseOverviewRequest("", "name_asc", 1, 10),
            canViewGrades: true,
            canViewAverage: true,
            refreshQueued: false,
            skewSeconds: null,
            now: DateTimeOffset.UtcNow);

    private static MoodleSnapshotEnvelope<CourseParticipantsPage> Students(
        IReadOnlyList<CourseParticipantSummary> participants,
        bool complete = true,
        bool hasMore = false) =>
        new(new CourseParticipantsPage(
            "42", 1, 500, ParticipantStatusFilter.Active, true, false, hasMore, participants,
            new ParticipantClassificationDiagnostics(participants.Count, participants.Count, 0, 0, false, false, ParticipantClassificationMode.RoleBased)),
            DateTimeOffset.UtcNow,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: complete,
            RecordCount: participants.Count);

    private static CourseParticipantSummary Student(string id, string name, DateTimeOffset? lastAccess) =>
        new(id, name, null, false, null, null, lastAccess, [], [], "active");

    private static MoodleSnapshotEnvelope<CourseGradebookSnapshot> Gradebook(
        IReadOnlyCollection<GradebookItemDefinition> items,
        IReadOnlyCollection<StudentGradebookEntry> grades,
        bool complete) =>
        new(new CourseGradebookSnapshot(
                "42",
                new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase),
                new GradebookSnapshotCoverage("bulk", grades.Select(item => item.StudentId).Distinct().Count(), grades.Select(item => item.StudentId).Distinct().Count(), complete, false, [], [])
                {
                    ReturnedStudentIds = grades.Select(item => item.StudentId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                })
            {
                Items = items,
                StudentGrades = grades,
            },
            DateTimeOffset.UtcNow,
            IsStale: false,
            IsFrozen: false,
            Tier: "hot",
            IsComplete: complete);
}
