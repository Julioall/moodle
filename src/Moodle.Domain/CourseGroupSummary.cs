namespace Moodle.Domain;

public sealed record CourseGroupSummary(
    string GroupId,
    string CourseId,
    string Name,
    string? IdNumber);
