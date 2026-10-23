namespace Moodle.Infrastructure;

public sealed class MoodleCallDiagnosticEntity
{
    public string EventId { get; set; } = string.Empty;
    public string CallId { get; set; } = string.Empty;
    public string? RetryGroupId { get; set; }
    public string? AttemptId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public int CatalogVersion { get; set; }
    public string? CorrelationId { get; set; }
    public string? SnapshotRunId { get; set; }
    public string? SnapshotRunItemId { get; set; }
    public string ConnectionKey { get; set; } = string.Empty;
    public string MoodleFunction { get; set; } = string.Empty;
    public string OperationKind { get; set; } = string.Empty;
    public string? ErrorCategory { get; set; }
    public string? ClassificationReason { get; set; }
    public string? MoodleErrorCode { get; set; }
    public string? MoodleExceptionType { get; set; }
    public string? SafeMessage { get; set; }
    public string CodeState { get; set; } = string.Empty;
    public string ExceptionState { get; set; } = string.Empty;
    public int? HttpStatus { get; set; }
    public string ResponseKind { get; set; } = string.Empty;
    public string? TransportErrorType { get; set; }
    public int? SocketErrorCode { get; set; }
    public long DurationMs { get; set; }
    public int? AttemptNumber { get; set; }
    public int AttemptCount { get; set; }
    public string? RetryDisposition { get; set; }
    public long? RetryDelayMs { get; set; }
    public string? Outcome { get; set; }
    public string? LastFailureEventId { get; set; }
    public string RawPayloadState { get; set; } = "not_available";
    public long? RawPayloadSizeBytes { get; set; }
    public string? RawPayloadSha256 { get; set; }
    public string? RawPayloadContentType { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
