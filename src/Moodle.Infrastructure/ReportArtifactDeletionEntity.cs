namespace Moodle.Infrastructure;

public sealed class ReportArtifactDeletionEntity
{
    public Guid Id { get; set; }
    public string ArtifactBlobKey { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}
