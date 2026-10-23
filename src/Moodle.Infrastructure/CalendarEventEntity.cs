namespace Moodle.Infrastructure;

public sealed class CalendarEventEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public string TimeZoneId { get; set; } = "America/Sao_Paulo";
    public string? Location { get; set; }
    public string AvailabilityStatus { get; set; } = "busy";
    public bool IsAllDay { get; set; }
    public string Source { get; set; } = "manual";
    public long Version { get; set; } = 1;
    public string Type { get; set; } = "other";
    public string? ExternalUid { get; set; }
    public string? ExternalSource { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
