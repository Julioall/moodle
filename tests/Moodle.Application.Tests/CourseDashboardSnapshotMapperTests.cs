using Moodle.Domain;
using Moodle.Presentation;

namespace Moodle.Application.Tests;

public sealed class CourseDashboardSnapshotMapperTests
{
    [Fact]
    public void Builds_course_indicators_from_its_own_snapshot_rows_only()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var dashboard = CourseDashboardSnapshotMapper.Create(
            Course("course-1"),
            Participants(now),
            PendingSnapshot(),
            connectionRef: "demo",
            todayEvents: 1,
            todayTasks: 2,
            generatedAt: now,
            week: AppDashboardWeekFilter.Current,
            weekStartsAt: now.AddDays(-1),
            weekEndsAt: now.AddDays(6),
            refreshQueued: false);

        Assert.Equal(2, dashboard.Summary.ActiveStudents);
        Assert.Equal(1, dashboard.Summary.StudentsAtRisk);
        Assert.Equal(2, dashboard.Summary.StudentsNeedingAttention);
        Assert.Equal(1, dashboard.Summary.PendingSubmissionAssignments);
        Assert.Equal(1, dashboard.Summary.PendingCorrectionAssignments);
        Assert.Equal(3, dashboard.Priorities.Count);
        Assert.All(dashboard.Priorities, item => Assert.Equal("course-1", item.CourseId));
        Assert.Single(dashboard.ActivitiesToReview);
    }

    [Fact]
    public void Keeps_unavailable_snapshot_indicators_explicit()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var dashboard = CourseDashboardSnapshotMapper.Create(
            Course("course-1"),
            participants: null,
            pendingSnapshot: null,
            connectionRef: "demo",
            todayEvents: 0,
            todayTasks: 0,
            generatedAt: now,
            week: AppDashboardWeekFilter.Current,
            weekStartsAt: now,
            weekEndsAt: now.AddDays(7),
            refreshQueued: true);

        Assert.Null(dashboard.Summary.ActiveStudents);
        Assert.Null(dashboard.Summary.PendingSubmissionAssignments);
        Assert.Null(dashboard.Summary.PendingCorrectionAssignments);
        Assert.Contains(dashboard.Warnings, warning => warning.Contains("está sendo preparada", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dashboard.Warnings, warning => warning.Contains("Atualização solicitada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rebuilds_pending_submission_priorities_from_pending_items_when_priority_rows_are_missing()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var dashboard = CourseDashboardSnapshotMapper.Create(
            Course("course-1"),
            Participants(now),
            PendingSnapshotWithoutPriorityRows(),
            connectionRef: "demo",
            todayEvents: 0,
            todayTasks: 0,
            generatedAt: now,
            week: AppDashboardWeekFilter.Current,
            weekStartsAt: now,
            weekEndsAt: now.AddDays(7),
            refreshQueued: false);

        Assert.Equal(1, dashboard.Summary.PendingSubmissionAssignments);
        Assert.Contains(dashboard.Priorities, item =>
            item.CourseId == "course-1" &&
            item.StudentId == "student-2" &&
            item.Title == "Entrega pendente");
    }

    [Fact]
    public void Keeps_course_counts_explicit_when_priority_rows_are_still_being_prepared()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var dashboard = CourseDashboardSnapshotMapper.Create(
            Course("course-1"),
            Participants(now),
            PendingSnapshotWithMissingCorrectionRows(),
            connectionRef: "demo",
            todayEvents: 0,
            todayTasks: 0,
            generatedAt: now,
            week: AppDashboardWeekFilter.Current,
            weekStartsAt: now,
            weekEndsAt: now.AddDays(7),
            refreshQueued: false);

        Assert.Equal(2, dashboard.Summary.PendingCorrectionAssignments);
        Assert.Contains(dashboard.Warnings, warning => warning.Contains("prioridades do curso", StringComparison.OrdinalIgnoreCase));
    }

    private static CourseSummary Course(string courseId) => new(
        courseId, null, "CURSO", "Curso de teste", null, null, null,
        null, null, true, null, null, null, null, null, null);

    private static CourseParticipantsPage Participants(DateTimeOffset now) => new(
        "course-1", 1, 25, ParticipantStatusFilter.Active, true, false, false,
        [
            new CourseParticipantSummary("student-1", "Alice", null, false, null, null, now.AddDays(-21), [], []),
            new CourseParticipantSummary("student-2", "Bruno", null, false, null, null, now.AddDays(-1), [], []),
        ]);

    private static AppDashboardPendingMetricDto PendingSnapshot() => new(
        new AppDashboardSummaryDto(2, 1, 1, 0, 1),
        [
            new AppDashboardPriorityDto("course-1:student-2:assign-1", "Entrega pendente", "Bruno · Atividade", "attention", "course-1", "student-2"),
            new AppDashboardPriorityDto("course-2:student-9:assign-9", "Entrega pendente", "Outro · Atividade", "risk", "course-2", "student-9"),
        ],
        [
            new AppDashboardPriorityDto("course-1:student-2:assign-1:grading", "Atividade para corrigir", "Bruno · Atividade", "attention", "course-1", "student-2"),
            new AppDashboardPriorityDto("course-2:student-9:assign-9:grading", "Atividade para corrigir", "Outro · Atividade", "attention", "course-2", "student-9"),
        ],
        [],
        [],
        [])
    {
        CoursesInScope = 2,
        CoursesAnalyzed = 2,
    };

    private static AppDashboardPendingMetricDto PendingSnapshotWithoutPriorityRows() => new(
        new AppDashboardSummaryDto(1, 1, 0, 0, 1),
        [],
        [],
        [
            new AppDashboardCoursePendingSummaryDto(
                "course-1",
                "Curso de teste",
                PendingCorrectionActivities: 0,
                PendingCorrectionSubmissions: 0,
                PendingSubmissionActivities: 1,
                PendingSubmissions: 1,
                StudentsAwaitingCorrection: 0,
                StudentsWithPendingSubmissions: 1,
                OverdueSubmissions: 0,
                IsTruncated: false,
                Warning: null),
        ],
        [],
        [])
    {
        CoursesInScope = 1,
        CoursesAnalyzed = 1,
        PendingItems =
        [
            new AppDashboardPendingItemDto(
                "course-1",
                "student-2",
                "Bruno",
                null,
                "assign-1",
                "Atividade",
                null,
                false),
        ],
    };

    private static AppDashboardPendingMetricDto PendingSnapshotWithMissingCorrectionRows() => new(
        new AppDashboardSummaryDto(1, 0, 2, 0, 0),
        [],
        [],
        [
            new AppDashboardCoursePendingSummaryDto(
                "course-1",
                "Curso de teste",
                PendingCorrectionActivities: 2,
                PendingCorrectionSubmissions: 2,
                PendingSubmissionActivities: 0,
                PendingSubmissions: 0,
                StudentsAwaitingCorrection: 1,
                StudentsWithPendingSubmissions: 0,
                OverdueSubmissions: 0,
                IsTruncated: false,
                Warning: null),
        ],
        [],
        [])
    {
        CoursesInScope = 1,
        CoursesAnalyzed = 1,
    };
}
