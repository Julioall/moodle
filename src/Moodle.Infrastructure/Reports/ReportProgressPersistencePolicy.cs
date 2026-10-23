namespace Moodle.Infrastructure.Reports;

internal static class ReportProgressPersistencePolicy
{
    private const int PersistenceIntervalCourses = 5;

    public static bool ShouldPersist(int processedCourses, int totalCourses) =>
        processedCourses > 0 &&
        (processedCourses % PersistenceIntervalCourses == 0 || processedCourses >= totalCourses);
}
