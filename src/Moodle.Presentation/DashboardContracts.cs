namespace Moodle.Presentation;

public sealed record AppDashboardSummaryDto(
    int ActiveCourses,
    int PendingDeliveries,
    int AwaitingGrading,
    int StudentsAtRisk,
    int StudentsNeedingAttention)
{
    // Claris-compatible indicators. Nullable values are intentional when the
    // connector has no bounded source for a metric yet.
    public int? TodayEvents { get; init; }
    public int? TodayTasks { get; init; }
    public int? ActivitiesToReview { get; init; }
    public int? ActiveNormalStudents { get; init; }
    public int? PendingSubmissionAssignments { get; init; }
    public int? PendingCorrectionAssignments { get; init; }
    public int? NewAtRiskThisWeek { get; init; }
    public int? ActiveStudents { get; init; }
    public int? NeverAccessedStudents { get; init; }
}

public sealed record AppDashboardPriorityDto(
    string Key,
    string Title,
    string Detail,
    string Level,
    string? CourseId,
    string? StudentId);

public sealed record AppDashboardActivityDto(
    string Key,
    string Title,
    string Detail,
    DateTimeOffset? OccurredAt,
    string? CourseId,
    string? StudentId);

public sealed record AppDashboardDto(
    AppDashboardSummaryDto Summary,
    IReadOnlyList<AppDashboardPriorityDto> Priorities,
    IReadOnlyList<AppDashboardPriorityDto> ActivitiesToReview,
    IReadOnlyList<AppDashboardActivityDto> RecentActivity,
    string? ConnectionRef,
    IReadOnlyList<string> Warnings)
{
    public string Week { get; init; } = AppDashboardWeekFilter.Current;
    public DateTimeOffset? WeekStartsAt { get; init; }
    public DateTimeOffset? WeekEndsAt { get; init; }
}

public sealed record AppDashboardSummaryMetricDto(
    AppDashboardSummaryDto Summary,
    IReadOnlyList<string> Warnings);

public sealed record AppDashboardPendingMetricDto(
    AppDashboardSummaryDto Summary,
    IReadOnlyList<AppDashboardPriorityDto> Priorities,
    IReadOnlyList<AppDashboardPriorityDto> ActivitiesToReview,
    IReadOnlyList<AppDashboardCoursePendingSummaryDto> CourseSummaries,
    IReadOnlyList<AppDashboardTodayItemDto> TodayItems,
    IReadOnlyList<string> Warnings)
{
    public bool IsRefreshing { get; init; }
    public int CoursesInScope { get; init; }
    public int CoursesAnalyzed { get; init; }
    public DateTimeOffset? SnapshotGeneratedAt { get; init; }
    public IReadOnlyList<AppDashboardPendingItemDto> PendingItems { get; init; } = [];
}

public sealed record AppDashboardCoursePendingSummaryDto(
    string CourseId,
    string CourseName,
    int PendingCorrectionActivities,
    int PendingCorrectionSubmissions,
    int PendingSubmissionActivities,
    int PendingSubmissions,
    int StudentsAwaitingCorrection,
    int StudentsWithPendingSubmissions,
    int OverdueSubmissions,
    bool IsTruncated,
    string? Warning);

public sealed record AppDashboardTodayItemDto(
    string Key,
    string Kind,
    string Title,
    string? Detail,
    DateTimeOffset? StartsAt);

public sealed record AppDashboardPendingItemDto(
    string CourseId,
    string StudentId,
    string StudentName,
    DateTimeOffset? LastCourseAccessAt,
    string AssignmentId,
    string AssignmentName,
    DateTimeOffset? DueAt,
    bool IsOverdue);

public sealed record AppDashboardAccessMetricDto(
    AppDashboardSummaryDto Summary,
    IReadOnlyList<AppDashboardAccessSegmentDto> Segments,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<AppDashboardAccessSnapshotDto> Snapshots { get; init; } = [];
}

public sealed record AppDashboardAccessSegmentDto(
    string Key,
    string Label,
    int Students,
    string Tone);

public sealed record AppDashboardAccessSnapshotDto(
    DateOnly Date,
    int TotalStudents,
    int RecentStudents,
    int LowAccessStudents,
    int StaleStudents,
    int NeverAccessedStudents,
    int StudentsAtRisk);

public sealed record AppDashboardCoursesMetricDto(
    IReadOnlyList<AppCourseDto> Courses,
    IReadOnlyList<string> Warnings);

public static class AppDashboardWeekFilter
{
    public const string Current = "current";
    public const string Last = "last";

    public static string Normalize(string? value) =>
        string.Equals(value, Last, StringComparison.OrdinalIgnoreCase) ? Last : Current;
}

public static class AppDashboardBudget
{
    public static readonly TimeSpan MetricCacheDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CourseScopeCacheDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PendingSnapshotFreshness = TimeSpan.FromHours(24);
    public const int MaxCoursesRead = 50;
    // The pending overview runs asynchronously, so it can afford a broader
    // read budget than the synchronous course screens.
    public const int MaxParticipantsRead = 500;
    public const int MaxAssignmentsRead = 100;
    public const int PendingCourseConcurrency = 4;
    public const int MaxPriorities = 50;
    public const int MaxCorrectionItems = 500;
    public const int MaxActivities = 50;
}

public static class AppDashboardContractMapper
{
    public static AppDashboardDto Empty(string? connectionRef, IEnumerable<string> warnings, IReadOnlyList<AppDashboardActivityDto>? recentActivity = null) => new(
        new(0, 0, 0, 0, 0), [], [], recentActivity ?? [], connectionRef,
        warnings.Distinct(StringComparer.Ordinal).ToArray());
}
