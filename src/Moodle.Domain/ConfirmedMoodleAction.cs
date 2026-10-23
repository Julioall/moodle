namespace Moodle.Domain;

public sealed class ConfirmedMoodleAction
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid PendingActionId { get; init; }

    public string ToolName { get; init; } = string.Empty;

    public string ConfirmedBySubject { get; init; } = string.Empty;

    public DateTimeOffset ConfirmedAt { get; init; } = DateTimeOffset.UtcNow;

    public string CorrelationId { get; init; } = string.Empty;
}
