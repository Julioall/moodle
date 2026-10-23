namespace Moodle.Infrastructure;

public sealed class TaskEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "todo";
    public string Priority { get; set; } = "medium";
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? ParentTaskId { get; set; }
    public Guid? CreatedBy { get; set; }
    public long Version { get; set; } = 1;
    public string? ActionType { get; set; }
    public string? ScheduleHint { get; set; }
    public string? ExternalUid { get; set; }
    public string? ExternalSource { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
