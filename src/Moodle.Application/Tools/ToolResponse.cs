using System.Text.Json.Serialization;

namespace Moodle.Application.Tools;

public sealed record ToolFreshness(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("snapshotAt")] DateTimeOffset? SnapshotAt,
    [property: JsonPropertyName("ageSeconds")] long? AgeSeconds,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("refreshQueued")] bool RefreshQueued,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("recordCount")] int RecordCount,
    [property: JsonPropertyName("decisionSafe")] bool DecisionSafe = true,
    [property: JsonPropertyName("dataset")] string? Dataset = null,
    [property: JsonPropertyName("recordType")] string? RecordType = null);

public sealed record ToolResponse<T>
{
    public ToolResponse(
        string Status,
        T? Data,
        IReadOnlyList<string> Warnings,
        string? AuditId,
        DateTimeOffset Timestamp,
        string? ErrorCode = null,
        string? Message = null,
        ToolFreshness? Freshness = null,
        string? ErrorPhase = null,
        string? NextAction = null,
        string? Code = null,
        string? CauseCode = null,
        bool? Retryable = null,
        int? RetryAfterSeconds = null)
    {
        this.Status = Status;
        this.Data = Data;
        this.Warnings = Warnings;
        this.AuditId = string.IsNullOrWhiteSpace(AuditId) ? Guid.NewGuid().ToString("N") : AuditId;
        this.Timestamp = Timestamp;
        this.ErrorCode = ErrorCode;
        this.Message = Message;
        this.Freshness = Freshness;
        this.ErrorPhase = ErrorPhase;
        this.NextAction = NextAction;
        this.Code = Code;
        this.CauseCode = CauseCode;
        this.Retryable = Retryable;
        this.RetryAfterSeconds = RetryAfterSeconds;
    }

    [JsonPropertyName("status")]
    public string Status { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; }

    [JsonPropertyName("auditId")]
    public string AuditId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("freshness")]
    public ToolFreshness? Freshness { get; init; }

    [JsonPropertyName("phase")]
    public string? ErrorPhase { get; init; }

    [JsonPropertyName("nextAction")]
    public string? NextAction { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("causeCode")]
    public string? CauseCode { get; init; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; init; }

    [JsonPropertyName("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; init; }
}
