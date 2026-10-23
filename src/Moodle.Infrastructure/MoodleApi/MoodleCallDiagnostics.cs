using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure.MoodleApi;

internal sealed record MoodleCallDiagnosticRecord(
    string EventId,
    string CallId,
    string? AttemptId,
    string EventType,
    int SchemaVersion,
    int CatalogVersion,
    string? CorrelationId,
    string? SnapshotRunId,
    string? SnapshotRunItemId,
    string ConnectionKey,
    string MoodleFunction,
    string OperationKind,
    string? ErrorCategory,
    string? ClassificationReason,
    string? MoodleErrorCode,
    string? MoodleExceptionType,
    string? SafeMessage,
    string CodeState,
    string ExceptionState,
    int? HttpStatus,
    string ResponseKind,
    string? TransportErrorType,
    int? SocketErrorCode,
    long DurationMs,
    int? AttemptNumber,
    int AttemptCount,
    string? RetryDisposition,
    long? RetryDelayMs,
    string? Outcome,
    DateTimeOffset CreatedAtUtc)
{
    public string? LastFailureEventId { get; init; }
    public string? RetryGroupId { get; init; }
    public string RawPayloadState { get; init; } = "not_available";
    public long? RawPayloadSizeBytes { get; init; }
    public string? RawPayloadSha256 { get; init; }
    public string? RawPayloadContentType { get; init; }

    // Raw response bytes are persisted only to the restricted payload table.
    // They must never enter stdout or structured diagnostic JSON.
    [JsonIgnore]
    public byte[]? RawPayload { get; init; }
}

internal static class MoodleCallDiagnosticHttpOptions
{
    public static readonly HttpRequestOptionsKey<MoodleCallDiagnosticsState> State =
        new("Moodle.Diagnostics.State");
}

internal sealed class MoodleCallDiagnosticsState
{
    internal const int MaxRawErrorPayloadBytes = 1024 * 1024;
    private static readonly AsyncLocal<MoodleCallDiagnosticsState?> Ambient = new();
    private readonly object _gate = new();
    private readonly List<MoodleAttemptObservation> _attempts = [];
    private readonly string _callId;
    private readonly string? _correlationId;
    private readonly string? _snapshotRunId;
    private readonly string? _snapshotRunItemId;
    private readonly string? _retryGroupId;
    private readonly bool _hasPriorExternalRetryFailure;
    private readonly string _connectionKey;
    private readonly string _function;
    private readonly string _operationKind;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private MoodleCallDiagnosticClassification? _terminalFailure;
    private string _outcome = "unknown";
    private int? _finalHttpStatus;
    private string _finalResponseKind = "unknown";
    private RawErrorPayload? _terminalRawPayload;
    private IReadOnlyList<MoodleCallDiagnosticRecord>? _builtRecords;

    public static MoodleCallDiagnosticsState? Current => Ambient.Value;

    public int AttemptCount
    {
        get
        {
            lock (_gate) return _attempts.Count;
        }
    }

    public int? LastObservedHttpStatus
    {
        get
        {
            lock (_gate)
                return _attempts.LastOrDefault(attempt => attempt.HttpStatus is not null)?.HttpStatus;
        }
    }

    public IDisposable Enter()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new RestoreAmbientScope(previous);
    }

    public MoodleCallDiagnosticsState(
        string callId,
        string? connectionId,
        string? functionName,
        string? operationKind)
    {
        _callId = MoodleCallDiagnosticCatalog.NormalizeGuid(callId);
        _correlationId = Activity.Current?.TraceId.ToHexString();
        (_snapshotRunId, _snapshotRunItemId) = MoodleCallDiagnosticsCorrelationScope.Current;
        _retryGroupId = MoodleReadRetryCorrelationScope.RetryGroupId;
        _hasPriorExternalRetryFailure = MoodleReadRetryCorrelationScope.HasPriorTransientFailure;
        _connectionKey = MoodleCallDiagnosticCatalog.ConnectionKey(connectionId);
        _function = MoodleCallDiagnosticCatalog.FunctionName(functionName);
        _operationKind = operationKind is "read" or "write" ? operationKind : "unknown";
    }

    public int StartAttempt()
    {
        lock (_gate)
        {
            _builtRecords = null;
            var attempt = new MoodleAttemptObservation(
                Guid.NewGuid().ToString("N"),
                _attempts.Count + 1,
                Stopwatch.GetTimestamp());
            _attempts.Add(attempt);
            return attempt.Number;
        }
    }

    public void CompleteResponse(int attemptNumber, HttpResponseMessage response)
    {
        lock (_gate)
        {
            if (!TryGetAttempt(attemptNumber, out var attempt)) return;
            attempt.HttpStatus = (int)response.StatusCode;
            attempt.ResponseKind = MoodleCallDiagnosticCatalog.ResponseKind(
                response.Content.Headers.ContentType?.MediaType,
                response.Content.Headers.ContentLength);
            attempt.DurationMs = ElapsedMs(attempt.StartedAtTimestamp);
            attempt.IsFailure = !response.IsSuccessStatusCode;
            _builtRecords = null;
        }
    }

    public void SetResponseBodyKind(string? contentType, string? payload)
    {
        lock (_gate)
        {
            _finalResponseKind = string.IsNullOrWhiteSpace(payload)
                ? "empty"
                : MoodleCallDiagnosticCatalog.ResponseKind(contentType, null);
            if (_attempts.Count > 0)
            {
                _attempts[^1].ResponseKind = _finalResponseKind;
                _attempts[^1].ResponseBodyReadCompleted = true;
            }
            _builtRecords = null;
        }
    }

    public void CaptureErrorPayload(byte[] payload, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var size = (long)payload.Length;
        var normalizedContentType = NormalizeRawPayloadContentType(contentType);
        var payloadCapture = size <= MaxRawErrorPayloadBytes
            ? new RawErrorPayload("captured", size, Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), normalizedContentType, payload)
            : new RawErrorPayload("oversize", size, Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), normalizedContentType, null);

        lock (_gate)
        {
            if (_attempts.Count > 0)
                _attempts[^1].RawPayload = payloadCapture;
            else
                _terminalRawPayload = payloadCapture;
            _builtRecords = null;
        }
    }

    public void MarkErrorPayloadOversize(long sizeBytes, string? contentType)
    {
        lock (_gate)
        {
            var payloadCapture = new RawErrorPayload(
                "oversize",
                Math.Max(0, sizeBytes),
                null,
                NormalizeRawPayloadContentType(contentType),
                null);
            if (_attempts.Count > 0)
                _attempts[^1].RawPayload = payloadCapture;
            else
                _terminalRawPayload = payloadCapture;
            _builtRecords = null;
        }
    }

    public void CompleteTransportFailure(int attemptNumber, Exception exception, bool callerCancelled)
    {
        lock (_gate)
        {
            if (!TryGetAttempt(attemptNumber, out var attempt)) return;
            attempt.DurationMs = ElapsedMs(attempt.StartedAtTimestamp);
            attempt.IsCancelled = callerCancelled;
            if (!callerCancelled)
            {
                attempt.IsFailure = true;
                attempt.TransportErrorType = MoodleCallDiagnosticCatalog.TransportErrorType(exception);
                attempt.SocketErrorCode = MoodleCallDiagnosticCatalog.SocketErrorCode(exception);
            }
            _builtRecords = null;
        }
    }

    public void MarkRetryScheduled(int attemptNumber, TimeSpan delay)
    {
        lock (_gate)
        {
            if (!TryGetAttempt(attemptNumber, out var attempt)) return;
            attempt.RetryScheduled = true;
            attempt.RetryDelayMs = Math.Max(0, (long)delay.TotalMilliseconds);
            _builtRecords = null;
        }
    }

    public void MarkSucceeded(int? httpStatus, string responseKind)
    {
        lock (_gate)
        {
            _outcome = _attempts.Any(attempt => attempt.IsFailure)
                ? "succeeded_after_retry"
                : "succeeded";
            _finalHttpStatus = httpStatus;
            _finalResponseKind = MoodleCallDiagnosticCatalog.NormalizeResponseKind(responseKind);
            _builtRecords = null;
        }
    }

    public void MarkFailure(
        MoodleApiException exception,
        string? responseKind = null,
        bool circuitOpen = false)
    {
        lock (_gate)
        {
            _outcome = circuitOpen ? "circuit_open" : "failed";
            _finalHttpStatus = circuitOpen
                ? exception.HttpStatusCode
                : exception.HttpStatusCode ?? _attempts.LastOrDefault(attempt => attempt.HttpStatus is not null)?.HttpStatus;
            if (circuitOpen)
            {
                _finalResponseKind = "unknown";
            }
            if (responseKind is not null)
                _finalResponseKind = MoodleCallDiagnosticCatalog.NormalizeResponseKind(responseKind);
            _terminalFailure = circuitOpen
                ? MoodleCallDiagnosticCatalog.ClassifyCircuitOpen()
                : MoodleCallDiagnosticCatalog.Classify(exception);
            if (_attempts.Count > 0 && !circuitOpen)
            {
                var attempt = _attempts[^1];
                attempt.IsFailure = true;
                attempt.HttpStatus ??= exception.HttpStatusCode;
                if (_finalResponseKind != "unknown")
                    attempt.ResponseKind = _finalResponseKind;
                attempt.TransportErrorType ??= _terminalFailure.TransportErrorType;
                attempt.SocketErrorCode ??= _terminalFailure.SocketErrorCode;
                attempt.FinalClassification = _terminalFailure;
                attempt.DurationMs = Math.Max(attempt.DurationMs, ElapsedMs(attempt.StartedAtTimestamp));
            }
            _builtRecords = null;
        }
    }

    public void MarkCancelled()
    {
        lock (_gate)
        {
            _outcome = "cancelled";
            var lastObservedResponse = _attempts.LastOrDefault(attempt => attempt.HttpStatus is not null);
            _finalHttpStatus ??= lastObservedResponse?.HttpStatus;
            if (_finalResponseKind == "unknown" && lastObservedResponse is not null)
            {
                _finalResponseKind = lastObservedResponse.ResponseKind;
            }
            if (_attempts.Count > 0 && !_attempts[^1].ResponseBodyReadCompleted &&
                _attempts[^1].HttpStatus is not null)
            {
                var incompleteResponse = _attempts[^1];
                incompleteResponse.IsFailure = false;
                incompleteResponse.IsCancelled = true;
                incompleteResponse.TransportErrorType = null;
                incompleteResponse.SocketErrorCode = null;
                incompleteResponse.FinalClassification = null;
            }
            else if (_attempts.Count > 0 && _attempts[^1].TransportErrorType == "timeout" &&
                     _attempts[^1].HttpStatus is null)
            {
                _attempts[^1].IsFailure = false;
                _attempts[^1].IsCancelled = true;
                _attempts[^1].TransportErrorType = null;
                _attempts[^1].SocketErrorCode = null;
            }
            _builtRecords = null;
        }
    }

    public IReadOnlyList<MoodleCallDiagnosticRecord> BuildRecords()
    {
        lock (_gate)
        {
            if (_builtRecords is not null) return _builtRecords;
            var failures = _attempts.Where(attempt => attempt.IsFailure).ToArray();
            var shouldRecord = failures.Length > 0 ||
                               _outcome is "failed" or "circuit_open" or "cancelled" ||
                               _outcome == "succeeded" && _hasPriorExternalRetryFailure;
            if (!shouldRecord)
            {
                return _builtRecords = [];
            }

            var now = DateTimeOffset.UtcNow;
            var records = new List<MoodleCallDiagnosticRecord>(failures.Length + 1);
            string? lastFailureEventId = null;
            foreach (var attempt in failures)
            {
                var classification = attempt.FinalClassification ??
                                     MoodleCallDiagnosticCatalog.Classify(
                                         attempt.HttpStatus,
                                         attempt.TransportErrorType,
                                         attempt.SocketErrorCode);
                var eventId = Guid.NewGuid().ToString("N");
                lastFailureEventId = eventId;
                records.Add(new MoodleCallDiagnosticRecord(
                    eventId,
                    _callId,
                    attempt.AttemptId,
                    "attempt_failed",
                    MoodleCallDiagnosticCatalog.SchemaVersion,
                    MoodleCallDiagnosticCatalog.CatalogVersion,
                    _correlationId,
                    _snapshotRunId,
                    _snapshotRunItemId,
                    _connectionKey,
                    _function,
                    _operationKind,
                    classification.ErrorCategory,
                    classification.ClassificationReason,
                    classification.MoodleErrorCode,
                    classification.MoodleExceptionType,
                    classification.SafeMessage,
                    classification.CodeState,
                    classification.ExceptionState,
                    attempt.HttpStatus,
                    attempt.ResponseKind,
                    attempt.TransportErrorType ?? classification.TransportErrorType,
                    attempt.SocketErrorCode ?? classification.SocketErrorCode,
                    Math.Max(0, attempt.DurationMs),
                    attempt.Number,
                    _attempts.Count,
                    attempt.RetryScheduled ? "scheduled" : "not_scheduled",
                    attempt.RetryDelayMs,
                    null,
                    now)
                {
                    RetryGroupId = _retryGroupId,
                    RawPayloadState = attempt.RawPayload?.State ?? "not_available",
                    RawPayloadSizeBytes = attempt.RawPayload?.SizeBytes,
                    RawPayloadSha256 = attempt.RawPayload?.Sha256,
                    RawPayloadContentType = attempt.RawPayload?.ContentType,
                    RawPayload = attempt.RawPayload?.Bytes
                });
            }

            var terminal = _terminalFailure;
            records.Add(new MoodleCallDiagnosticRecord(
                Guid.NewGuid().ToString("N"),
                _callId,
                null,
                "call_completed",
                MoodleCallDiagnosticCatalog.SchemaVersion,
                MoodleCallDiagnosticCatalog.CatalogVersion,
                _correlationId,
                _snapshotRunId,
                _snapshotRunItemId,
                _connectionKey,
                _function,
                _operationKind,
                terminal?.ErrorCategory,
                terminal?.ClassificationReason,
                terminal?.MoodleErrorCode,
                terminal?.MoodleExceptionType,
                terminal?.SafeMessage,
                terminal?.CodeState ?? "absent",
                terminal?.ExceptionState ?? "absent",
                _finalHttpStatus,
                _finalResponseKind,
                terminal?.TransportErrorType,
                terminal?.SocketErrorCode,
                ElapsedMs(_startedAt),
                null,
                _attempts.Count,
                null,
                null,
                _outcome == "succeeded" && _hasPriorExternalRetryFailure
                    ? "succeeded_after_external_retry"
                    : _outcome,
                now)
            {
                LastFailureEventId = lastFailureEventId,
                RetryGroupId = _retryGroupId,
                RawPayloadState = _terminalRawPayload?.State ?? "not_available",
                RawPayloadSizeBytes = _terminalRawPayload?.SizeBytes,
                RawPayloadSha256 = _terminalRawPayload?.Sha256,
                RawPayloadContentType = _terminalRawPayload?.ContentType,
                RawPayload = _terminalRawPayload?.Bytes
            });
            return _builtRecords = records;
        }
    }

    private bool TryGetAttempt(int number, out MoodleAttemptObservation attempt)
    {
        if (number > 0 && number <= _attempts.Count)
        {
            attempt = _attempts[number - 1];
            return true;
        }

        attempt = null!;
        return false;
    }

    private static long ElapsedMs(long timestamp) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);

    private static string? NormalizeRawPayloadContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var normalized = contentType.Trim();
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    private sealed class RestoreAmbientScope(MoodleCallDiagnosticsState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }

    private sealed class MoodleAttemptObservation(string attemptId, int number, long startedAtTimestamp)
    {
        public string AttemptId { get; } = attemptId;
        public int Number { get; } = number;
        public long StartedAtTimestamp { get; } = startedAtTimestamp;
        public int? HttpStatus { get; set; }
        public string ResponseKind { get; set; } = "unknown";
        public string? TransportErrorType { get; set; }
        public int? SocketErrorCode { get; set; }
        public long DurationMs { get; set; }
        public bool IsFailure { get; set; }
        public bool IsCancelled { get; set; }
        public bool ResponseBodyReadCompleted { get; set; }
        public bool RetryScheduled { get; set; }
        public long? RetryDelayMs { get; set; }
        public MoodleCallDiagnosticClassification? FinalClassification { get; set; }
        public RawErrorPayload? RawPayload { get; set; }
    }

    private sealed record RawErrorPayload(string State, long SizeBytes, string? Sha256, string? ContentType, byte[]? Bytes);
}

internal sealed record MoodleCallDiagnosticClassification(
    string ErrorCategory,
    string? ClassificationReason,
    string? MoodleErrorCode,
    string? MoodleExceptionType,
    string SafeMessage,
    string CodeState,
    string ExceptionState,
    string? TransportErrorType = null,
    int? SocketErrorCode = null);

internal static class MoodleCallDiagnosticCatalog
{
    public const int SchemaVersion = 1;
    public const int CatalogVersion = 1;

    private static readonly IReadOnlyDictionary<string, string> RemoteErrorCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["invalidtoken"] = "invalidtoken",
            ["invalid_token"] = "invalid_token",
            ["wstokeninvalid"] = "wstokeninvalid",
            ["accessexception"] = "accessexception",
            ["access_exception"] = "access_exception",
            ["webservice_access_exception"] = "webservice_access_exception",
            ["nopermissions"] = "nopermissions",
            ["not_enrolled"] = "not_enrolled",
            ["invalidparameter"] = "invalidparameter",
            ["invalid_parameter"] = "invalid_parameter",
            ["invalidfunction"] = "invalidfunction",
            ["unknownfunction"] = "unknownfunction",
            ["functionnotavailable"] = "functionnotavailable",
            ["invalidcourseid"] = "invalidcourseid",
            ["course_not_found"] = "course_not_found",
            ["invalidactivityid"] = "invalidactivityid",
            ["invalidcoursemodule"] = "invalidcoursemodule",
        };

    private static readonly IReadOnlyDictionary<string, string> RemoteExceptionTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["moodle_exception"] = "moodle_exception",
            ["moodle_access_exception"] = "moodle_access_exception",
            ["required_capability_exception"] = "required_capability_exception",
            ["webservice_access_exception"] = "webservice_access_exception",
            ["invalid_parameter_exception"] = "invalid_parameter_exception",
            ["moodle_invalid_parameter_exception"] = "moodle_invalid_parameter_exception",
            ["invalid_function_exception"] = "invalid_function_exception",
        };

    private static readonly HashSet<string> KnownFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "core_calendar_create_calendar_events",
        "core_course_get_categories",
        "core_course_get_contents",
        "core_course_get_courses",
        "core_course_get_courses_by_field",
        "core_course_get_enrolled_courses_by_timeline_classification",
        "core_course_search_courses",
        "core_enrol_get_enrolled_users",
        "core_enrol_get_users_courses",
        "core_files_get_files",
        "core_grading_get_definitions",
        "core_group_get_course_groups",
        "core_message_get_conversation_between_users",
        "core_message_get_conversations",
        "core_message_send_instant_messages",
        "core_message_send_messages_to_conversation",
        "core_reportbuilder_filters_reset",
        "core_reportbuilder_list_reports",
        "core_reportbuilder_retrieve_report",
        "core_reportbuilder_set_filters",
        "core_user_get_users_by_field",
        "core_user_get_course_user_profiles",
        "core_webservice_get_site_info",
        "gradereport_user_get_grade_items",
        "gradereport_user_get_grades_table",
        "mod_assign_get_assignments",
        "mod_assign_get_grades",
        "mod_assign_get_participant",
        "mod_assign_get_submission_status",
        "mod_assign_get_submissions",
        "mod_assign_get_user_flags",
        "mod_assign_save_grade",
        "mod_assign_save_grades",
        "mod_forum_add_discussion",
        "mod_forum_add_discussion_post",
        "mod_forum_can_add_discussion",
        "mod_forum_get_discussion_posts",
        "mod_forum_get_forum_discussions",
        "mod_forum_get_forum_discussions_paginated",
        "mod_forum_get_forums_by_courses",
        "mod_quiz_get_quizzes_by_courses",
        "mod_scorm_get_scorms_by_courses",
    };

    public static string NormalizeGuid(string value) =>
        Guid.TryParse(value, out var parsed) ? parsed.ToString("N") : Guid.NewGuid().ToString("N");

    public static string ConnectionKey(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return "unknown";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(connectionId));
        return "c_" + Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static string FunctionName(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName)) return "unknown";
        var normalized = functionName.Trim().ToLowerInvariant();
        return normalized.Length <= 64 &&
               normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') &&
               KnownFunctions.Contains(normalized)
            ? normalized
            : "unknown";
    }

    public static string ResponseKind(string? mediaType, long? contentLength)
    {
        if (contentLength == 0) return "empty";
        if (mediaType is null) return "unknown";
        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) return "json";
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)) return "html";
        return "other";
    }

    public static string NormalizeResponseKind(string? responseKind) => responseKind switch
    {
        "json" or "html" or "empty" or "other" => responseKind,
        _ => "unknown"
    };

    public static string? NormalizeRemoteErrorCode(string? value) =>
        value is not null && RemoteErrorCodes.TryGetValue(value, out var safe) ? safe : null;

    public static string? NormalizeRemoteExceptionType(string? value) =>
        value is not null && RemoteExceptionTypes.TryGetValue(value, out var safe) ? safe : null;

    public static MoodleCallDiagnosticClassification Classify(MoodleApiException exception)
    {
        var code = NormalizeRemoteErrorCode(exception.RemoteErrorCode);
        var exceptionType = NormalizeRemoteExceptionType(exception.RemoteExceptionType);
        var codeState = GetValueState(
            exception.RemoteErrorCode,
            exception.RemoteErrorCodePresent,
            code is not null,
            64);
        var exceptionState = GetValueState(
            exception.RemoteExceptionType,
            exception.RemoteExceptionTypePresent,
            exceptionType is not null,
            96);

        if (exception.Stage == MoodleIntegrationStage.UrlValidation)
            return Make("internal_error", code, exceptionType, codeState, exceptionState, "endpoint_validation");
        if (exception.Stage == MoodleIntegrationStage.TokenRequest)
            return ClassifyTokenAcquisition(exception, code, exceptionType, codeState, exceptionState);

        if (exception.ErrorCode == MoodleErrorContract.RequestTimeout)
            return Make("transport_timeout", code, exceptionType, codeState, exceptionState,
                "timeout", SocketErrorCode(exception.InnerException), "timeout");
        if (exception.ErrorCode == MoodleErrorContract.NetworkError &&
            exception.InnerException is HttpRequestException or SocketException or OperationCanceledException or IOException)
        {
            var transportType = TransportErrorType(exception.InnerException);
            var transportCategory = transportType switch
            {
                "timeout" => "transport_timeout",
                "connection_reset" => "transport_connection_reset",
                "dns" => "transport_dns",
                "tls" => "transport_tls",
                _ => "transport_other"
            };
            return Make(transportCategory, code, exceptionType, codeState, exceptionState, null,
                SocketErrorCode(exception.InnerException), transportType);
        }

        if (exception.HttpStatusCode is 429)
            return Make("http_rate_limited", code, exceptionType, codeState, exceptionState);
        if (exception.HttpStatusCode is >= 500)
            return Make("http_server_error", code, exceptionType, codeState, exceptionState);
        if (exception.HttpStatusCode is >= 400)
            return Make("http_client_error", code, exceptionType, codeState, exceptionState);

        var remoteCategory = RemoteCategory(exception.RemoteErrorCode, exception.RemoteExceptionType);
        if (remoteCategory is not null)
            return Make(remoteCategory, code, exceptionType, codeState, exceptionState);

        if (exception.ErrorCode == MoodleErrorContract.InvalidResponse)
            return Make("unexpected_response", code, exceptionType, codeState, exceptionState);
        if (exception.ErrorCode is MoodleErrorContract.MoodleAdmissionRejected or
            MoodleErrorContract.ReadQueueFull or MoodleErrorContract.ReadQueueTimeout or
            MoodleErrorContract.ReadConnectionCapacity)
            return Make("internal_error", code, exceptionType, codeState, exceptionState, "local_admission");
        if (exception.ErrorCode == MoodleErrorContract.AuthenticationFailed)
            return Make("moodle_authentication_error", code, exceptionType, codeState, exceptionState, "local_authentication");
        if (exception.ErrorCode == MoodleErrorContract.PermissionDenied)
            return Make("moodle_permission_denied", code, exceptionType, codeState, exceptionState, "local_permission");
        if (exception.ErrorCode == MoodleErrorContract.FunctionNotAllowed)
            return Make("moodle_invalid_function", code, exceptionType, codeState, exceptionState, "local_function_policy");
        if (exception.ErrorCode == MoodleErrorContract.NetworkError)
        {
            var transportType = TransportErrorType(exception.InnerException);
            var category = transportType switch
            {
                "timeout" => "transport_timeout",
                "connection_reset" => "transport_connection_reset",
                "dns" => "transport_dns",
                "tls" => "transport_tls",
                _ => "transport_other"
            };
            return Make(category, code, exceptionType, codeState, exceptionState, null,
                SocketErrorCode(exception.InnerException), transportType);
        }

        return exception.ErrorCode == MoodleErrorContract.ApiError
            ? Make("moodle_api_error_unknown", code, exceptionType, codeState, exceptionState)
            : Make("internal_error", code, exceptionType, codeState, exceptionState, "local_failure");
    }

    private static MoodleCallDiagnosticClassification ClassifyTokenAcquisition(
        MoodleApiException exception,
        string? code,
        string? exceptionType,
        string codeState,
        string exceptionState)
    {
        if (exception.ErrorCode == MoodleErrorContract.RequestTimeout)
            return Make("transport_timeout", code, exceptionType, codeState, exceptionState,
                "token_acquisition", SocketErrorCode(exception.InnerException), "timeout");

        if (exception.ErrorCode == MoodleErrorContract.NetworkError &&
            exception.InnerException is HttpRequestException or SocketException or OperationCanceledException or IOException)
        {
            var transportType = TransportErrorType(exception.InnerException);
            var category = transportType switch
            {
                "timeout" => "transport_timeout",
                "connection_reset" => "transport_connection_reset",
                "dns" => "transport_dns",
                "tls" => "transport_tls",
                _ => "transport_other"
            };
            return Make(category, code, exceptionType, codeState, exceptionState,
                "token_acquisition", SocketErrorCode(exception.InnerException), transportType);
        }

        if (exception.HttpStatusCode is 429)
            return Make("http_rate_limited", code, exceptionType, codeState, exceptionState, "token_acquisition");
        if (exception.HttpStatusCode is >= 500)
            return Make("http_server_error", code, exceptionType, codeState, exceptionState, "token_acquisition");
        if (exception.HttpStatusCode is >= 400)
            return Make("http_client_error", code, exceptionType, codeState, exceptionState, "token_acquisition");
        if (exception.ErrorCode == MoodleErrorContract.AuthenticationFailed)
            return Make("moodle_authentication_error", code, exceptionType, codeState, exceptionState, "token_acquisition");
        if (exception.ErrorCode == MoodleErrorContract.InvalidResponse)
            return Make("unexpected_response", code, exceptionType, codeState, exceptionState, "token_acquisition");
        if (exception.ErrorCode == MoodleErrorContract.ApiError)
            return Make("moodle_api_error_unknown", code, exceptionType, codeState, exceptionState, "token_acquisition");
        return Make("internal_error", code, exceptionType, codeState, exceptionState, "token_acquisition");
    }

    public static MoodleCallDiagnosticClassification ClassifyCircuitOpen() =>
        Make("internal_error", null, null, "absent", "absent", "circuit_open");

    public static MoodleCallDiagnosticClassification Classify(
        int? httpStatus,
        string? transportErrorType,
        int? socketErrorCode)
    {
        if (transportErrorType is not null)
        {
            var category = transportErrorType switch
            {
                "timeout" => "transport_timeout",
                "connection_reset" => "transport_connection_reset",
                "dns" => "transport_dns",
                "tls" => "transport_tls",
                _ => "transport_other"
            };
            return Make(category, null, null, "absent", "absent", null, socketErrorCode, transportErrorType);
        }
        if (httpStatus is 429) return Make("http_rate_limited", null, null, "absent", "absent");
        if (httpStatus is >= 500) return Make("http_server_error", null, null, "absent", "absent");
        if (httpStatus is >= 400 or < 200) return Make("http_client_error", null, null, "absent", "absent");
        return Make("unexpected_response", null, null, "absent", "absent");
    }

    public static string TransportErrorType(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException) return "timeout";
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionReset or SocketError.ConnectionAborted => "connection_reset",
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => "dns",
                    _ => "network"
                };
            }
            if (current.GetType().Name.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
                current.GetType().Name.Contains("Ssl", StringComparison.OrdinalIgnoreCase)) return "tls";
        }
        return "network";
    }

    public static int? SocketErrorCode(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket) return (int)socket.SocketErrorCode;
        }
        return null;
    }

    private static string? RemoteCategory(string? errorCode, string? exceptionType)
    {
        var code = errorCode?.ToLowerInvariant();
        if (code is "invalidtoken" or "invalid_token" or "wstokeninvalid") return "moodle_authentication_error";
        if (code is "accessexception" or "access_exception" or "webservice_access_exception" or "nopermissions" or "not_enrolled" ||
            exceptionType is "moodle_access_exception" or "required_capability_exception" or "webservice_access_exception")
            return "moodle_permission_denied";
        if (code is "invalidparameter" or "invalid_parameter" ||
            exceptionType is "invalid_parameter_exception" or "moodle_invalid_parameter_exception")
            return "moodle_invalid_parameter";
        if (code is "invalidfunction" or "unknownfunction" or "functionnotavailable" ||
            exceptionType == "invalid_function_exception") return "moodle_invalid_function";
        if (code is "invalidcourseid" or "course_not_found") return "moodle_api_error_unknown";
        if (exceptionType == "moodle_exception") return "moodle_api_error_unknown";
        return code is not null || exceptionType is not null ? "moodle_api_error_unknown" : null;
    }

    private static string GetValueState(string? raw, bool present, bool recognized, int maxLength) =>
        !present ? "absent" : raw is null || raw.Length == 0 || raw.Length > maxLength ||
                              !raw.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? "invalid"
            : recognized ? "known" : "unknown";

    private static MoodleCallDiagnosticClassification Make(
        string category,
        string? code,
        string? exceptionType,
        string codeState,
        string exceptionState,
        string? reason = null,
        int? socketCode = null,
        string? transportType = null) =>
        new(category, reason, code, exceptionType, SafeMessage(category),
            codeState, exceptionState, transportType, socketCode);

    private static string SafeMessage(string category) => category switch
    {
        "transport_timeout" => "A tentativa Moodle excedeu o tempo limite.",
        "transport_connection_reset" => "A conexão com o Moodle foi encerrada durante a chamada.",
        "transport_dns" => "O nome do servidor Moodle não pôde ser resolvido.",
        "transport_tls" => "A conexão segura com o Moodle falhou.",
        "transport_other" => "O transporte da chamada Moodle falhou.",
        "http_rate_limited" => "O Moodle limitou temporariamente as chamadas.",
        "http_server_error" => "O servidor Moodle respondeu com erro.",
        "http_client_error" => "O Moodle respondeu com um erro HTTP da chamada.",
        "moodle_permission_denied" => "O Moodle recusou a chamada por falta de permissão.",
        "moodle_authentication_error" => "O Moodle recusou a autenticação da chamada.",
        "moodle_invalid_function" => "A função solicitada não está disponível no Moodle.",
        "moodle_invalid_parameter" => "O Moodle recusou os parâmetros da chamada.",
        "moodle_api_error_unknown" => "O Moodle retornou um erro de Web Service não catalogado.",
        "unexpected_response" => "O Moodle retornou uma resposta incompatível com o contrato esperado.",
        "internal_error" => "A aplicação não conseguiu concluir a chamada Moodle.",
        _ => "A aplicação encontrou uma falha não classificada na chamada Moodle."
    };
}

internal static class MoodleCallDiagnosticsCorrelationScope
{
    private static readonly AsyncLocal<(string? RunId, string? RunItemId)> Ambient = new();

    public static (string? RunId, string? RunItemId) Current => Ambient.Value;

    public static IDisposable Begin(Guid runId, Guid runItemId)
    {
        var previous = Ambient.Value;
        Ambient.Value = (runId.ToString("N"), runItemId.ToString("N"));
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope((string? RunId, string? RunItemId) previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
