namespace Moodle.Domain;

/// <summary>
/// A short-lived, opaque handle to a Moodle-owned file.  The remote reference
/// is intentionally persistence-only: it must never be sent in an MCP result.
/// </summary>
public sealed class MoodleResource
{
    public string ResourceId { get; init; } = Guid.NewGuid().ToString("N");
    public string ClientId { get; init; } = string.Empty;
    public string ConnectionId { get; init; } = string.Empty;
    public string MoodleAlias { get; init; } = string.Empty;
    /// <summary>Usuário da aplicação que originou o resource efêmero.</summary>
    public string OwnerSubject { get; init; } = string.Empty;
    public string ResourceType { get; init; } = "submission_attachment";
    public long? CourseId { get; init; }
    public long? AssignmentId { get; init; }
    public long? SubmissionId { get; init; }
    public long? StudentId { get; init; }
    public string? ContextId { get; init; }
    public string? Component { get; init; }
    public string? FileArea { get; init; }
    public string? ItemId { get; init; }
    public string Filename { get; init; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long? SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public string RemoteFileReference { get; init; } = string.Empty;
    public string? ParentResourceId { get; init; }
    /// <summary>Temporary bytes for a safely extracted child resource. Never serialized into tool output.</summary>
    public byte[]? InlineContent { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsExpired(DateTimeOffset now) => RevokedAt is not null || ExpiresAt <= now;

    public void RecordIntegrity(string mimeType, long sizeBytes, string sha256)
    {
        MimeType = string.IsNullOrWhiteSpace(mimeType) ? MimeType : mimeType.Trim().ToLowerInvariant();
        SizeBytes = sizeBytes;
        Sha256 = sha256.Trim().ToLowerInvariant();
    }
}
