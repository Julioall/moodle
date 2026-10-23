namespace Moodle.Presentation;

public sealed record FollowupDto(Guid Id, string StudentRef, string? StudentName, string? CourseRef, string Kind, string Notes, DateTimeOffset OccurredAt, DateTimeOffset CreatedAt)
{
    public string? Reason { get; init; }
    public string? Action { get; init; }
    public string? Status { get; init; }
    public string? ActorName { get; init; }
}

public sealed record FollowupInput(string StudentRef, string? StudentName, string? CourseRef, string Kind, string Notes, DateTimeOffset? OccurredAt)
{
    public string? Reason { get; init; }
    public string? Action { get; init; }
    public string? Status { get; init; }
}
public sealed record AppMessagePrepareInput(string CourseId, string MessageType, IReadOnlyList<string> RecipientIds, string? CustomText);
public sealed record AppMessageConfirmInput(Guid PendingActionId, string ConfirmationText);
