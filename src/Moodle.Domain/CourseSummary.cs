namespace Moodle.Domain;

public sealed record CourseSummary(
    string CourseId,
    string? IdNumber,
    string? ShortName,
    string FullName,
    string? DisplayName,
    long? CategoryId,
    string? CategoryName,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    bool? Visible,
    string? ViewUrl,
    string? CourseImage,
    decimal? Progress,
    bool? HasProgress,
    bool? IsFavourite,
    DateTimeOffset? LastAccessAt);
