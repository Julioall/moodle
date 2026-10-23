namespace Moodle.Infrastructure;

public sealed class PortalEvidenceEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string? ConnectionAlias { get; set; }
    public string CourseId { get; set; } = string.Empty;
    public string? StudentId { get; set; }
    public string? ActivityId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Source { get; set; } = "moodle";
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
