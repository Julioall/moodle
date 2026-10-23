namespace Moodle.Infrastructure;

public sealed class TaskParticipantEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "collaborator";
    public DateTimeOffset AssignedAt { get; set; }
    public Guid AssignedBy { get; set; }
}

public sealed class TaskReferenceEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string ReferenceType { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public string? ReferenceName { get; set; }
    public string? ConnectionRef { get; set; }
    public string? Relation { get; set; }
}

public sealed class TaskTagEntity
{
    public Guid TaskId { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
}

public sealed class TaskCommentEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid AuthorId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
}

public sealed class TaskActivityEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid ActorId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TaskDependencyEntity
{
    public Guid TaskId { get; set; }
    public Guid DependsOnTaskId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EventRecurrenceEntity
{
    public Guid EventId { get; set; }
    public string RRule { get; set; } = string.Empty;
    public DateTimeOffset? UntilAt { get; set; }
    public int? Count { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EventRecurrenceDateEntity
{
    public Guid EventId { get; set; }
    public DateTimeOffset OccurrenceStartAt { get; set; }
    public string Kind { get; set; } = "exclude";
}

public sealed class EventOccurrenceOverrideEntity
{
    public Guid EventId { get; set; }
    public DateTimeOffset OriginalStartAt { get; set; }
    public bool IsCancelled { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EventReferenceEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string ReferenceType { get; set; } = string.Empty;
    public string ReferenceId { get; set; } = string.Empty;
    public string? ReferenceName { get; set; }
    public string? ConnectionRef { get; set; }
    public string? Relation { get; set; }
}

public sealed class EventTagEntity
{
    public Guid EventId { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
}

public sealed class TaskEventLinkEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset? OccurrenceStartAt { get; set; }
    public string Relation { get; set; } = "related";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
