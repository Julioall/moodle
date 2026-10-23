namespace Moodle.Infrastructure;

/// <summary>
/// Append-only, sanitized evidence for one durable grading publication
/// transition. Academic values remain in the private grading item/payload.
/// </summary>
public sealed class GradingPublicationAttemptEventEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PendingActionId { get; set; }

    public Guid PublicationId { get; set; }

    public Guid GradingItemId { get; set; }

    public int? SubmissionAttemptNumber { get; set; }

    public int DispatchAttemptNumber { get; set; }

    public Guid? DispatchGroupId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string ItemState { get; set; } = string.Empty;

    public string? WorkerId { get; set; }

    public long? FencingToken { get; set; }

    public string? ErrorCode { get; set; }

    public string? EvidenceKind { get; set; }

    public DateTimeOffset? RemoteObservedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
