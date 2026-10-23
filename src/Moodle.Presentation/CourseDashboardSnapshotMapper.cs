using Moodle.Domain;

namespace Moodle.Presentation;

/// <summary>
/// Builds the course workspace indicators from persisted snapshots so opening a
/// course never fans out into Moodle submission reads.
/// </summary>
internal static class CourseDashboardSnapshotMapper
{
    public static AppDashboardDto Create(
        CourseSummary course,
        CourseParticipantsPage? participants,
        AppDashboardPendingMetricDto? pendingSnapshot,
        string connectionRef,
        int todayEvents,
        int todayTasks,
        DateTimeOffset generatedAt,
        string week,
        DateTimeOffset weekStartsAt,
        DateTimeOffset weekEndsAt,
        bool refreshQueued)
    {
        var activeParticipants = participants?.Participants ?? [];
        var inactiveStudentIds = activeParticipants
            .Where(student => student.LastCourseAccessAt is null || student.LastCourseAccessAt < generatedAt.AddDays(-14))
            .Select(student => student.UserId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingCoverage = pendingSnapshot is null
            ? null
            : DashboardPendingCoveragePolicy.Evaluate(pendingSnapshot, course.CourseId);
        var pendingRows = BuildPendingRows(pendingSnapshot, course.CourseId);
        var gradingRows = pendingSnapshot?.ActivitiesToReview
            .Where(item => IsForCourse(item, course.CourseId))
            .OrderBy(item => item.Detail, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var pendingStudentIds = pendingRows
            .Where(item => !string.IsNullOrWhiteSpace(item.StudentId))
            .Select(item => item.StudentId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overdueStudentIds = pendingRows
            .Where(item => string.Equals(item.Level, "risk", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.StudentId))
            .Select(item => item.StudentId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var studentsAtRisk = inactiveStudentIds.Union(overdueStudentIds, StringComparer.OrdinalIgnoreCase).Count();
        var studentsNeedingAttention = inactiveStudentIds.Union(pendingStudentIds, StringComparer.OrdinalIgnoreCase).Count();
        var accessRows = activeParticipants
            .Where(student => inactiveStudentIds.Contains(student.UserId))
            .Select(student => new AppDashboardPriorityDto(
                $"{connectionRef}:{course.CourseId}:{student.UserId}:risk",
                "Aluno em risco",
                $"{student.FullName} · sem acesso recente",
                "risk",
                course.CourseId,
                student.UserId));
        var warnings = new List<string>();
        if (participants is null)
        {
            warnings.Add("A lista de alunos está sendo preparada em segundo plano.");
        }
        else if (participants.HasMore)
        {
            warnings.Add("O indicador de alunos está limitado ao orçamento de leitura do dashboard.");
        }

        if (pendingSnapshot is null)
        {
            warnings.Add("As pendências do curso estão sendo preparadas em segundo plano.");
        }
        else
        {
            if (pendingCoverage?.HasMissingCoverage == true)
            {
                warnings.Add("As prioridades do curso estão sendo preparadas em segundo plano.");
            }

            if (!pendingSnapshot.IsRefreshing && pendingSnapshot.CoursesAnalyzed < pendingSnapshot.CoursesInScope)
            {
                warnings.Add("As pendências deste curso podem estar incompletas.");
            }

            warnings.AddRange(pendingSnapshot.Warnings.Where(warning => IsCourseWarning(warning, course.CourseId)));
            var courseWarning = pendingSnapshot.CourseSummaries
                .FirstOrDefault(item => string.Equals(item.CourseId, course.CourseId, StringComparison.OrdinalIgnoreCase))
                ?.Warning;
            if (!string.IsNullOrWhiteSpace(courseWarning)) warnings.Add(courseWarning);
        }

        if (refreshQueued)
        {
            warnings.Add("Atualização solicitada; os indicadores serão atualizados assim que o Moodle responder.");
        }

        var summary = new AppDashboardSummaryDto(
            course.Visible == false ? 0 : 1,
            pendingRows.Length,
            gradingRows.Length,
            studentsAtRisk,
            studentsNeedingAttention)
        {
            TodayEvents = todayEvents,
            TodayTasks = todayTasks,
            ActivitiesToReview = gradingRows.Length,
            ActiveNormalStudents = participants is null ? null : Math.Max(0, activeParticipants.Count - studentsNeedingAttention),
            PendingSubmissionAssignments = pendingSnapshot is null
                ? null
                : pendingCoverage?.SubmissionItemsMissing == true
                    ? pendingCoverage.Summary?.PendingSubmissionActivities ?? pendingRows.Length
                    : pendingRows.Length,
            PendingCorrectionAssignments = pendingSnapshot is null
                ? null
                : pendingCoverage?.CorrectionItemsMissing == true
                    ? pendingCoverage.Summary?.PendingCorrectionActivities ?? gradingRows.Length
                    : gradingRows.Length,
            ActiveStudents = participants?.Participants.Count,
        };
        var priorities = accessRows
            .Concat(pendingRows)
            .Concat(gradingRows)
            .OrderByDescending(item => string.Equals(item.Level, "risk", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Detail, StringComparer.OrdinalIgnoreCase)
            .Take(AppDashboardBudget.MaxPriorities)
            .ToArray();
        var recent = pendingRows
            .Take(AppDashboardBudget.MaxActivities)
            .Select(item => new AppDashboardActivityDto(item.Key, item.Title, item.Detail, null, item.CourseId, item.StudentId))
            .ToArray();

        return new AppDashboardDto(summary, priorities, gradingRows, recent, connectionRef, warnings.Distinct(StringComparer.Ordinal).ToArray())
        {
            Week = week,
            WeekStartsAt = weekStartsAt,
            WeekEndsAt = weekEndsAt,
        };
    }

    private static bool IsForCourse(AppDashboardPriorityDto item, string courseId) =>
        string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase);

    private static bool IsCourseWarning(string warning, string courseId) =>
        warning.StartsWith($"[{courseId}]", StringComparison.OrdinalIgnoreCase);

    private static AppDashboardPriorityDto[] BuildPendingRows(
        AppDashboardPendingMetricDto? pendingSnapshot,
        string courseId)
    {
        var priorityRows = pendingSnapshot?.Priorities
            .Where(item => IsForCourse(item, courseId) &&
                           string.Equals(item.Title, "Entrega pendente", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => string.Equals(item.Level, "risk", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Detail, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (priorityRows.Length > 0)
        {
            return priorityRows;
        }

        return pendingSnapshot?.PendingItems
            .Where(item => string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase))
            .Select(item => new AppDashboardPriorityDto(
                $"{item.CourseId}:{item.StudentId}:{item.AssignmentId}",
                "Entrega pendente",
                $"{item.StudentName} · {item.AssignmentName}",
                item.IsOverdue ? "risk" : "attention",
                item.CourseId,
                item.StudentId))
            .OrderByDescending(item => string.Equals(item.Level, "risk", StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Detail, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }
}
