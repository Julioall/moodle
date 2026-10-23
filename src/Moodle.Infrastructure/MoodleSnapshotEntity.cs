using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

public sealed class MoodleSnapshotEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public string ConnectionAlias { get; set; } = string.Empty;
    public string SnapshotType { get; set; } = string.Empty;
    public string CourseId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public Guid? LastRunId { get; set; }
    public string Tier { get; set; } = "hot";
    public bool IsFrozen { get; set; }
    public DateTimeOffset? FrozenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FreshUntil { get; set; }
    public DateTimeOffset? StaleUntil { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? PayloadHash { get; set; }
    public bool IsComplete { get; set; } = true;
    public int RecordCount { get; set; }
}

public sealed class MoodleSyncStateEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public string ConnectionAlias { get; set; } = string.Empty;
    public string Dataset { get; set; } = string.Empty;
    public string WorkPolicy { get; set; } = MoodleSnapshotWorkPolicies.Default;
    public string CourseId { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastCompletedAt { get; set; }
    public DateTimeOffset? NextSyncAt { get; set; }
    public string? LastError { get; set; }
    public int RecordsSynced { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string UserExternalId { get; set; } = string.Empty;
    public int Priority { get; set; } = 50;
    public DateTimeOffset? LeaseUntil { get; set; }
    public int AttemptCount { get; set; }
    public bool ForceRequested { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Technical journal for one snapshot synchronization attempt. It never
/// stores a source payload or analytical facts.
/// </summary>
public sealed class MoodleSnapshotRunEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string ConnectionId { get; set; } = string.Empty;
    public string ConnectionAlias { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
    public string Trigger { get; set; } = "scheduled";
    public string WorkerId { get; set; } = string.Empty;
    public string SynchronizerVersion { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int ItemsTotal { get; set; }
    public int ItemsSucceeded { get; set; }
    public int ItemsFailed { get; set; }
    public int RecordsSynced { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MoodleSnapshotRunItemEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Dataset { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
    public int Attempts { get; set; } = 1;
    public string? PayloadHash { get; set; }
    public long PayloadSizeBytes { get; set; }
    public int RecordCount { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
