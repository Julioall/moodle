using Moodle.Domain;

public sealed record AppCourseRef(string ConnectionRef, string CourseId);
public sealed record AppCourseHierarchyNodeDto(string Path, string Name, int Level, int CourseCount);
public sealed record AppCourseDto(
    string ConnectionRef, string CourseId, string? IdNumber, string? ShortName,
    string FullName, string? DisplayName, long? CategoryId, string? CategoryName,
    DateTimeOffset? StartDate, DateTimeOffset? EndDate, bool? Visible,
    string? ViewUrl, string? CourseImage, decimal? Progress, bool? HasProgress,
    bool? IsFavourite, DateTimeOffset? LastAccessAt);
public sealed record AppActivityDto(
    string ConnectionRef, string CourseId, string ActivityId, string? InstanceId,
    string ActivityType, string Name, string? Url, bool? Visible, bool? UserVisible,
    string? Description, string? AvailabilityInfo, bool HasDates, bool HasDeadline,
    DateTimeOffset? OpenAt, DateTimeOffset? DueAt, DateTimeOffset? CloseAt, int FileCount)
{
    public int PendingSubmissionCount { get; init; }
    public int AwaitingGradingCount { get; init; }
}

public static class AppCourseContractMapper
{
    public static AppCourseDto ToDto(CourseSummary course, string connectionRef) => new(
        connectionRef, course.CourseId, course.IdNumber, course.ShortName, course.FullName,
        course.DisplayName, course.CategoryId, course.CategoryName, course.StartDate, course.EndDate,
        course.Visible, course.ViewUrl, course.CourseImage, course.Progress,
        course.HasProgress, course.IsFavourite, course.LastAccessAt);

    public static AppActivityDto ToDto(CourseActivitySummary activity, string connectionRef, string courseId) => new(
        connectionRef, courseId, activity.ActivityId, activity.InstanceId, activity.ActivityType,
        activity.Name, activity.Url, activity.Visible, activity.UserVisible, activity.Description,
        activity.AvailabilityInfo, activity.HasDates, activity.HasDeadline, activity.OpenAt,
        activity.DueAt, activity.CloseAt, activity.FileCount);
}

public static class AppErrorResults
{
    public static IResult NotFound(string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: StatusCodes.Status404NotFound);
}
