using System.Text.Json.Serialization;

namespace Moodle.Domain;

public static class CourseActivityModuleTypes
{
    public static readonly IReadOnlyCollection<string> All = ["assign", "quiz", "scorm", "forum"];

    public static readonly IReadOnlyCollection<string> Assignments = ["assign"];

    public static readonly IReadOnlyCollection<string> Quizzes = ["quiz"];

    public static readonly IReadOnlyCollection<string> Scorms = ["scorm"];
}

public sealed record CourseActivitiesSummary(
    string CourseId,
    IReadOnlyCollection<string> ActivityTypeFilters,
    bool IncludeHidden,
    int Total,
    int WithoutDatesCount,
    int WithoutDeadlineCount,
    IReadOnlyList<CourseActivitySummary> Activities)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record CourseActivitySummary(
    string ActivityId,
    string? InstanceId,
    string ActivityType,
    string Name,
    string? Url,
    bool? Visible,
    bool? UserVisible,
    string? Description,
    string? AvailabilityInfo,
    bool HasDates,
    bool HasDeadline,
    DateTimeOffset? OpenAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? CloseAt,
    IReadOnlyList<CourseModuleDate> Dates,
    int FileCount)
{
    public DateTimeOffset? CutoffAt { get; init; }
    public string DateSource { get; init; } = "unknown";
    public string DateCoverage { get; init; } = "not_returned";
    public AssignmentActivityDefinition? Assignment { get; init; }
    public bool DueAtKnown { get; init; }
}

public sealed record AssignmentActivityDefinition(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("coverage")] string Coverage,
    [property: JsonPropertyName("intro")] string? Intro,
    [property: JsonPropertyName("introFiles")] IReadOnlyList<CourseModuleFile>? IntroFiles,
    [property: JsonPropertyName("introFilesComplete")] bool? IntroFilesComplete,
    [property: JsonPropertyName("introAttachments")] IReadOnlyList<CourseModuleFile>? IntroAttachments,
    [property: JsonPropertyName("introAttachmentsComplete")] bool? IntroAttachmentsComplete,
    [property: JsonPropertyName("gradingMode")] string GradingMode,
    [property: JsonPropertyName("rawGrade")] decimal? RawGrade,
    [property: JsonPropertyName("maximumGrade")] decimal? MaximumGrade,
    [property: JsonPropertyName("scaleId")] long? ScaleId,
    [property: JsonPropertyName("scaleVersion")] string? ScaleVersion)
{
    [JsonPropertyName("scaleCoverage")] public string ScaleCoverage { get; init; } = "unknown";
}

public sealed record CourseActivityDeadlinesSummary(
    string CourseId,
    IReadOnlyCollection<string> ActivityTypeFilters,
    bool IncludeHidden,
    int Total,
    int WithoutDatesCount,
    int WithoutDeadlineCount,
    IReadOnlyList<CourseActivityDeadlineSummary> Deadlines)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record CourseActivityDeadlineSummary(
    string ActivityId,
    string? InstanceId,
    string ActivityType,
    string Name,
    bool? Visible,
    bool? UserVisible,
    bool HasDates,
    bool HasDeadline,
    DateTimeOffset? OpenAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? CloseAt,
    IReadOnlyList<CourseModuleDate> Dates)
{
    public DateTimeOffset? CutoffAt { get; init; }
    public string DateSource { get; init; } = "unknown";
    public string DateCoverage { get; init; } = "not_returned";
    public bool DueAtKnown { get; init; }
}
