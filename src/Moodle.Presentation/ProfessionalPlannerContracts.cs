namespace Moodle.Presentation;

public sealed record TaskParticipantDto(Guid UserId, string Role, DateTimeOffset AssignedAt);
public sealed record TaskReferenceV2Dto(Guid Id, string ReferenceType, string ReferenceId, string? ReferenceName, string? ConnectionRef, string? Relation);
public sealed record TaskProgressDto(int Done, int Total, decimal Percent);
public sealed record TaskListItemDto(Guid Id, string Title, string? Summary, string Status, string Priority, DateTimeOffset? DueAt, TaskParticipantDto? Owner, TaskProgressDto? SubtaskProgress, IReadOnlyList<TaskReferenceV2Dto> References, long Version, DateTimeOffset? StartAt = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null, string? ActionType = null, string? ScheduleHint = null)
{
    public IReadOnlyList<string>? Tags { get; init; }
}
public sealed record TaskDetailDto(Guid Id, string Title, string? Description, string Status, string Priority, DateTimeOffset? StartAt, DateTimeOffset? DueAt, DateTimeOffset? CompletedAt, Guid? ParentTaskId, IReadOnlyList<TaskParticipantDto> Participants, IReadOnlyList<TaskReferenceV2Dto> References, IReadOnlyList<string> Tags, IReadOnlyList<TaskListItemDto> Subtasks, TaskProgressDto? SubtaskProgress, IReadOnlyList<Guid> DependsOn, IReadOnlyList<Guid> Blocks, IReadOnlyList<TaskEventLinkDto> Events, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public TaskParticipantDto? Owner => Participants.FirstOrDefault(x => string.Equals(x.Role, "owner", StringComparison.OrdinalIgnoreCase));
    public string? ActionType { get; init; }
    public string? ScheduleHint { get; init; }
}
public sealed record TaskCommentDto(Guid Id, Guid AuthorId, string Content, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt);
public sealed record TaskActivityDto(Guid Id, Guid ActorId, string EventType, string? Data, DateTimeOffset CreatedAt);
public sealed record TaskTimelinePageDto(IReadOnlyList<TaskCommentDto> Comments, IReadOnlyList<TaskActivityDto> Activities, int Page, int PageSize, bool HasMore);
public sealed record TaskParticipantInput(Guid UserId, string Role);
public sealed record TaskSubtaskInput(string Title, string? Description = null, string? Priority = null, DateTimeOffset? DueAt = null, Guid? OwnerId = null);
public sealed record TaskReferenceV2Input(string ReferenceType, string ReferenceId, string? ReferenceName = null, string? ConnectionRef = null, string? Relation = null);
public sealed record TaskProfessionalInput(string? Title = null, string? Description = null, string? Status = null, string? Priority = null, DateTimeOffset? StartAt = null, DateTimeOffset? DueAt = null, Guid? ParentTaskId = null, IReadOnlyList<TaskParticipantInput>? Participants = null, IReadOnlyList<TaskReferenceV2Input>? References = null, IReadOnlyList<string>? Tags = null, long? ExpectedVersion = null, string? ActionType = null, string? ScheduleHint = null, bool ClearStartAt = false, bool ClearDueAt = false, IReadOnlyList<TaskSubtaskInput>? Subtasks = null, IReadOnlyList<Guid>? DependsOnTaskIds = null);
public sealed record DependencyInput(Guid DependsOnTaskId);
public sealed record EventRecurrenceInput(string? RRule = null, IReadOnlyList<DateTimeOffset>? ExDates = null, IReadOnlyList<DateTimeOffset>? RDates = null);
public sealed record EventReferenceV2Dto(Guid Id, string ReferenceType, string ReferenceId, string? ReferenceName, string? ConnectionRef, string? Relation);
public sealed record EventOccurrenceDto(Guid Id, DateTimeOffset OccurrenceStartAt, DateTimeOffset? OccurrenceEndAt, string Title, string? Description, string TimeZoneId, string? Location, string AvailabilityStatus, bool IsAllDay, bool IsCancelled, string? RRule, IReadOnlyList<string> Tags, IReadOnlyList<EventReferenceV2Dto> References, long Version)
{
    public string Type { get; init; } = "other";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
public sealed record EventDto(Guid Id, string Title, string? Description, DateTimeOffset StartAt, DateTimeOffset? EndAt, string Type, string TimeZoneId, string? Location, string AvailabilityStatus, bool IsAllDay, string Source, string? ExternalUid, string? RRule, IReadOnlyList<string> Tags, IReadOnlyList<EventReferenceV2Dto> References, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>Explicit recurrence exceptions included in the series contract.</summary>
    public IReadOnlyList<DateTimeOffset> ExDates { get; init; } = [];
    /// <summary>Explicit recurrence additions included in the series contract.</summary>
    public IReadOnlyList<DateTimeOffset> RDates { get; init; } = [];
}
public sealed record EventProfessionalInput(string? Title = null, string? Description = null, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null, string? TimeZoneId = null, string? Location = null, string? AvailabilityStatus = null, bool? IsAllDay = null, IReadOnlyList<string>? Tags = null, IReadOnlyList<TaskReferenceV2Input>? References = null, EventRecurrenceInput? Recurrence = null, long? ExpectedVersion = null, string? Type = null, bool ClearEndAt = false);
public sealed record OccurrenceOverrideInput(string? Title = null, string? Description = null, DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null, bool IsCancelled = false);
public sealed record TaskEventLinkDto(Guid Id, Guid TaskId, Guid EventId, DateTimeOffset? OccurrenceStartAt, string Relation, DateTimeOffset CreatedAt);
public sealed record TaskEventLinkInput(Guid EventId = default, DateTimeOffset? OccurrenceStartAt = null, string? Relation = null, string Mode = "link", DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null, EventRecurrenceInput? Recurrence = null);
public sealed record CreateEventFromTaskInput(DateTimeOffset StartAt, DateTimeOffset? EndAt = null, EventRecurrenceInput? Recurrence = null, string? Relation = "generated_from", string Mode = "create");
public sealed record CreateTaskFromEventInput(DateTimeOffset? DueAt = null, string? Relation = "generated_from", string Mode = "create", Guid? TaskId = null, DateTimeOffset? OccurrenceStartAt = null);
