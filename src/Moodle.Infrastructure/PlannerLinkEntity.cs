namespace Moodle.Infrastructure;

public sealed class PlannerLinkEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? CalendarEventId { get; set; }
    public string ReferenceType { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public string? ReferenceName { get; set; }
    public string? ConnectionRef { get; set; }
    public string? ParentReferenceType { get; set; }
    public string? ParentReferenceId { get; set; }
    public string? ParentReferenceName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
