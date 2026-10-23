namespace Moodle.Presentation;

public sealed record AppEvidenceDto(
    Guid Id,
    string? ConnectionRef,
    string CourseId,
    string? StudentId,
    string? ActivityId,
    string Kind,
    string Title,
    string Details,
    string Source,
    DateTimeOffset ObservedAt,
    DateTimeOffset CreatedAt);
