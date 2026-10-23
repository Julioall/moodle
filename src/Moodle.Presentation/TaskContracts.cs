namespace Moodle.Presentation;

public sealed record TaskDto(Guid Id, string Title, string? Description, string Status, string Priority, DateTimeOffset? StartAt, DateTimeOffset? DueAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PlannerReferenceDto>? References = null, string? ActionType = null, string? ScheduleHint = null)
{
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<TaskParticipantDto>? Participants { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long? Version { get; init; }
}
public sealed record TaskInput(string Title, string? Description, string? Status, string? Priority, DateTimeOffset? StartAt, DateTimeOffset? DueAt, IReadOnlyList<PlannerReferenceInput>? References = null, string? ActionType = null, string? ScheduleHint = null);
public sealed record TaskBulkDeleteInput(IReadOnlyCollection<Guid>? Ids);
public sealed record TaskBulkDeleteResult(int Requested, int Deleted);
