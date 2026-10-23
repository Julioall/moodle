namespace Moodle.Presentation;

public sealed record AppOperationalReportDto(int OpenTasks, int CompletedTasks, int UpcomingEvents, int FollowupsRecorded, DateTimeOffset GeneratedAt);
public sealed record AppAuditReportDto(int TotalActions, int CompletedActions, int FailedActions, int ConfirmedActions, DateTimeOffset GeneratedAt);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateReportJobInput(
    string? ReportType = null,
    string? ScopeType = null,
    string? ConnectionRef = null,
    string? CategoryPath = null,
    string? CourseId = null,
    IReadOnlyList<string>? CourseIds = null,
    Guid? TemplateId = null, long? ExpectedRevision = null, Guid? RefreshFromJobId = null);

public sealed record AppReportJobDto(
    Guid Id,
    string ReportType,
    string ScopeType,
    string ConnectionRef,
    string? CategoryPath,
    string? CourseId,
    string Status,
    int ProgressPercent,
    int TotalCourses,
    int ProcessedCourses,
    string? FileName,
    string? ContentType,
    long FileSizeBytes,
    string? ErrorMessage,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt,
    string? DownloadUrl,
    IReadOnlyList<AppReportCourseDto>? Courses = null,
    Guid? TemplateId = null, string? TemplateNameSnapshot = null, long? TemplateRevisionSnapshot = null,
    System.Text.Json.JsonElement? ResultMetadata = null);

public sealed record AppReportCourseDto(string Name, string? CategoryName);

public sealed record AppReportJobsEnvelope(
    IReadOnlyList<AppReportJobDto> Data,
    AppReportJobsMeta Meta);

public sealed record AppReportJobsMeta(
    int Page,
    int PageSize,
    int Returned,
    bool HasMore,
    DateTimeOffset GeneratedAt,
    string? ConnectionRef,
    IReadOnlyList<string>? Warnings,
    int? Total,
    long StorageUsedBytes,
    long StorageLimitBytes,
    long StorageAvailableBytes);

public sealed record AppCourseOverviewReportDto(
    string ConnectionRef, string CourseId, DateTimeOffset GeneratedAt, int TotalActiveStudents,
    int StudentsWhoAccessed, int StudentsNeverAccessed, int StudentsInactiveDays,
    int InactiveDaysThreshold, int? TotalGradedItems, decimal? AverageBelowMinimumPerStudent,
    IReadOnlyList<string> SuggestedActionsForTutor, string? Warning);

public sealed record AppWeeklyReportDto(
    string ConnectionRef, string CourseId, DateTimeOffset GeneratedAt, int TotalStudents,
    int StudentsWithAttention, int StudentsAtRisk, decimal MinGradePercent,
    int InactiveDaysThreshold, string? Warning);

public sealed record AppCompletionReportDto(
    string ConnectionRef, string CourseId, DateTimeOffset GeneratedAt, int TotalStudents,
    int LikelyComplete, int PendingRecovery, int AtRisk, int Unknown, decimal MinGradePercent,
    string Disclaimer, string? Warning);
