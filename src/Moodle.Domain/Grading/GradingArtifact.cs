namespace Moodle.Domain.Grading;

public sealed record GradingArtifact(
    Guid Id,
    Guid GradingItemId,
    string ArtifactType,
    string? Filename,
    string? MimeType,
    string? Sha256,
    long? SizeBytes,
    string ExtractionStatus,
    string? ExtractedTextRef,
    string? SummaryRef,
    DateTimeOffset CreatedAt,
    string? SourceUrl = null);
