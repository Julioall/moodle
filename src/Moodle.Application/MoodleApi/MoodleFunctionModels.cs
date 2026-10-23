using System.Text.Json;

namespace Moodle.Application.MoodleApi;

public enum MoodleFunctionRisk
{
    Unknown,
    Read,
    ControlledWrite,
    Destructive
}

public sealed record MoodleFunctionDescriptor(
    string Name,
    MoodleFunctionRisk Risk,
    bool IsAvailable);

public sealed record MoodleFunctionProfile(
    string ConnectionId,
    string ConnectionAlias,
    string? SiteName,
    string? Release,
    long? MoodleUserId,
    IReadOnlyList<MoodleFunctionDescriptor> Functions,
    DateTimeOffset DiscoveredAt,
    bool IsCached = false);

public sealed record MoodleFunctionResult(
    string Function,
    JsonElement Payload);

public enum MoodleIntegrationStage
{
    Unknown = 0,
    ConnectionLookup = 10,
    ConnectionState = 20,
    UrlValidation = 30,
    CredentialPresence = 40,
    CredentialDecryption = 50,
    TokenRequest = 60,
    MoodleRequest = 70,
    ResponseParsing = 80
}

public sealed class MoodleApiException : Exception
{
    public MoodleApiException(
        string errorCode,
        string message,
        int? httpStatusCode = null,
        Exception? innerException = null,
        string? auditId = null,
        string? connectionId = null,
        string? connectionAlias = null,
        string? endpoint = null,
        string? functionName = null,
        long? durationMs = null,
        string? remoteErrorCode = null,
        MoodleIntegrationStage stage = MoodleIntegrationStage.Unknown,
        TimeSpan? retryAfter = null,
        string? remoteExceptionType = null,
        bool remoteErrorCodePresent = false,
        bool remoteExceptionTypePresent = false)
        : base(message, innerException)
    {
        ErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? MoodleErrorContract.Unexpected
            : errorCode.Trim().ToLowerInvariant();
        HttpStatusCode = httpStatusCode;
        AuditId = string.IsNullOrWhiteSpace(auditId) ? Guid.NewGuid().ToString("N") : auditId;
        ConnectionId = connectionId;
        ConnectionAlias = connectionAlias;
        Endpoint = endpoint;
        FunctionName = functionName;
        DurationMs = durationMs;
        RemoteErrorCode = remoteErrorCode;
        RemoteErrorCodePresent = remoteErrorCodePresent || remoteErrorCode is not null;
        RemoteExceptionType = remoteExceptionType;
        RemoteExceptionTypePresent = remoteExceptionTypePresent || remoteExceptionType is not null;
        Stage = stage;
        RetryAfter = retryAfter;
    }

    public string ErrorCode { get; }
    public int? HttpStatusCode { get; }
    public string AuditId { get; }
    public string? ConnectionId { get; }
    public string? ConnectionAlias { get; }
    public string? Endpoint { get; }
    public string? FunctionName { get; }
    public long? DurationMs { get; }
    public string? RemoteErrorCode { get; }
    public bool RemoteErrorCodePresent { get; }
    public string? RemoteExceptionType { get; }
    public bool RemoteExceptionTypePresent { get; }
    public MoodleIntegrationStage Stage { get; }
    public TimeSpan? RetryAfter { get; }
}

public interface IMoodleFunctionCatalog
{
    Task<MoodleFunctionProfile> GetCurrentAsync(bool forceRefresh, CancellationToken cancellationToken);
}

public interface IMoodleFunctionExecutor
{
    Task<MoodleFunctionResult> ExecuteReadAsync(
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken);
}

public sealed record MoodleWritePreview(
    Guid PendingActionId,
    string Function,
    IReadOnlyList<string> ParameterNames,
    string ParameterHash,
    string ConfirmationText,
    DateTimeOffset ExpiresAt,
    string? SemanticSummary = null,
    IReadOnlyList<MoodleWritePreviewChange>? Changes = null,
    IReadOnlyList<string>? AffectedResources = null,
    int? EstimatedAffectedRecords = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record MoodleWritePreviewChange(
    string Name,
    string? PreviousValue,
    string? NewValue);

public sealed record MoodleWriteResult(
    string Status,
    Guid PendingActionId,
    string Function,
    string? AuditId,
    int ResponseSize);

public interface IMoodleUniversalWriteService
{
    Task<MoodleWritePreview> PrepareAsync(
        string functionName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken);

    Task<MoodleWriteResult> ConfirmAsync(
        Guid pendingActionId,
        string confirmationText,
        CancellationToken cancellationToken);
}
