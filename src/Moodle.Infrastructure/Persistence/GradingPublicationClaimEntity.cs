namespace Moodle.Infrastructure;

/// <summary>
/// Durable exclusion row for a Moodle grade target. The source correction item
/// remains historical; this row only serializes active publication attempts.
/// </summary>
public sealed class GradingPublicationClaimEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PublicationId { get; set; }

    /// <summary>
    /// Pending action that owns this publication. Binding the claim to the
    /// action lets expiry cleanup distinguish an abandoned preview from an
    /// already-authorized publication after a process crash.
    /// </summary>
    public Guid? PendingActionId { get; set; }

    public Guid GradingItemId { get; set; }

    public string ConnectionKey { get; set; } = string.Empty;

    public long AssignmentId { get; set; }

    public long MoodleUserId { get; set; }

    public int AttemptNumber { get; set; }

    /// <summary>The worker generation that currently owns this active claim.</summary>
    public string? WorkerId { get; set; }

    /// <summary>Monotonic pending-action execution generation; rejects stale checkpoints.</summary>
    public long? FencingToken { get; set; }

    public string Status { get; set; } = "AwaitingConfirmation";

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
