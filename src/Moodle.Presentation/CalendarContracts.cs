namespace Moodle.Presentation;

public sealed record CalendarEventDto(Guid Id, string Title, string? Description, DateTimeOffset StartAt, DateTimeOffset? EndAt, string Type, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PlannerReferenceDto>? References = null)
{
    public DateTimeOffset? OccurrenceStartAt { get; init; }
    public string? TimeZoneId { get; init; }
    public string? Location { get; init; }
    public string? AvailabilityStatus { get; init; }
    public bool IsAllDay { get; init; }
    public string? Source { get; init; }
    public string? ExternalUid { get; init; }
    public string? RRule { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public long? Version { get; init; }
}
public sealed record CalendarEventInput(string Title, string? Description, DateTimeOffset StartAt, DateTimeOffset? EndAt, string? Type, IReadOnlyList<PlannerReferenceInput>? References = null, string? TimeZoneId = null, string? Location = null, string? AvailabilityStatus = null, bool? IsAllDay = null, IReadOnlyList<string>? Tags = null, EventRecurrenceInput? Recurrence = null, long? ExpectedVersion = null);
public sealed record CalendarEventUpdateInput(string Title, string? Description, DateTimeOffset StartAt, DateTimeOffset? EndAt, string? Type, IReadOnlyList<PlannerReferenceInput>? References = null, string? TimeZoneId = null, string? Location = null, string? AvailabilityStatus = null, bool? IsAllDay = null, IReadOnlyList<string>? Tags = null, EventRecurrenceInput? Recurrence = null, long? ExpectedVersion = null, bool ClearEndAt = false);
