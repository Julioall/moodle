namespace Moodle.Presentation;

internal sealed record DashboardPendingCoverage(
    AppDashboardCoursePendingSummaryDto? Summary,
    bool SubmissionItemsMissing,
    bool CorrectionItemsMissing)
{
    public bool HasMissingCoverage => SubmissionItemsMissing || CorrectionItemsMissing;
}

internal static class DashboardPendingCoveragePolicy
{
    public static DashboardPendingCoverage Evaluate(AppDashboardPendingMetricDto snapshot, string courseId)
    {
        var summary = snapshot.CourseSummaries.FirstOrDefault(item =>
            string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase));

        if (summary is null)
        {
            return new DashboardPendingCoverage(null, false, false);
        }

        var expectsSubmissionRows =
            summary.PendingSubmissions > 0 ||
            summary.PendingSubmissionActivities > 0 ||
            summary.StudentsWithPendingSubmissions > 0 ||
            summary.OverdueSubmissions > 0;
        var expectsCorrectionRows =
            summary.PendingCorrectionSubmissions > 0 ||
            summary.PendingCorrectionActivities > 0 ||
            summary.StudentsAwaitingCorrection > 0;

        var hasSubmissionRows =
            snapshot.PendingItems.Any(item => string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase)) ||
            snapshot.Priorities.Any(item =>
                string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Title, "Entrega pendente", StringComparison.OrdinalIgnoreCase));
        var hasCorrectionRows =
            snapshot.ActivitiesToReview.Any(item => string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase)) ||
            snapshot.Priorities.Any(item =>
                string.Equals(item.CourseId, courseId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Title, "Atividade para corrigir", StringComparison.OrdinalIgnoreCase));

        return new DashboardPendingCoverage(
            summary,
            SubmissionItemsMissing: expectsSubmissionRows && !hasSubmissionRows,
            CorrectionItemsMissing: expectsCorrectionRows && !hasCorrectionRows);
    }
}
