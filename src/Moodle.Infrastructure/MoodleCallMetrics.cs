using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Infrastructure;

/// <summary>
/// Records aggregate Moodle Web Service costs without retaining request
/// parameters, credentials, student data, or other sensitive payloads.
/// </summary>
public sealed class MoodleCallMetrics : IMoodleCallTelemetry, IDisposable
{
    private readonly Meter meter = new("Moodle.MoodleCalls", "1.0.0");
    private readonly Counter<long> calls;
    private readonly Counter<long> failures;
    private readonly Counter<long> transportAttempts;
    private readonly Counter<long> diagnosticWriteEvents;
    private readonly Counter<long> diagnosticCleanupRows;
    private readonly Counter<long> diagnosticStdoutDroppedEvents;
    private readonly Histogram<long> diagnosticQueueDepth;
    private readonly Histogram<double> diagnosticQueueWaitMs;
    private readonly Counter<long> retries;
    private readonly Counter<long> logicalRetries;
    private readonly Counter<long> readGateEvents;
    private readonly Counter<long> forumPostReadBatches;
    private readonly Counter<long> scormParses;
    private readonly Histogram<double> durationMs;
    private readonly Histogram<double> readGateWaitMs;
    private readonly Histogram<long> readGateActive;
    private readonly Histogram<long> readGateQueueDepth;
    private readonly Histogram<long> forumPostReadDiscussions;
    private readonly Histogram<long> forumPostReadCalls;
    private readonly Histogram<long> forumPostReadFailures;
    private readonly Histogram<long> forumPostReadMaxConcurrency;
    private readonly Histogram<double> forumPostReadDurationMs;
    private readonly Histogram<long> scormParseContentEntryReads;
    private readonly Histogram<long> scormParseSelectedUncompressedBytes;
    private readonly Histogram<long> scormParseAllocatedBytes;
    private readonly Histogram<double> scormParseDurationMs;
    private readonly Histogram<long> scormParseScoCount;
    private readonly Histogram<long> scormParseFileCount;
    private readonly Histogram<long> requestPayloadBytes;
    private readonly Histogram<long> responsePayloadBytes;

    public MoodleCallMetrics()
    {
        calls = meter.CreateCounter<long>("moodle_webservice_calls");
        failures = meter.CreateCounter<long>("moodle_webservice_failures");
        transportAttempts = meter.CreateCounter<long>("moodle_webservice_transport_attempts");
        diagnosticWriteEvents = meter.CreateCounter<long>("moodle_diagnostic_write_events");
        diagnosticCleanupRows = meter.CreateCounter<long>("moodle_diagnostic_cleanup_rows");
        diagnosticStdoutDroppedEvents = meter.CreateCounter<long>("moodle_diagnostic_stdout_dropped_events");
        diagnosticQueueDepth = meter.CreateHistogram<long>("moodle_diagnostic_queue_depth");
        diagnosticQueueWaitMs = meter.CreateHistogram<double>("moodle_diagnostic_queue_wait_ms");
        retries = meter.CreateCounter<long>("moodle_webservice_retries");
        logicalRetries = meter.CreateCounter<long>("moodle_webservice_logical_retries");
        readGateEvents = meter.CreateCounter<long>("moodle_read_gate_events");
        forumPostReadBatches = meter.CreateCounter<long>("moodle_forum_post_read_batches");
        scormParses = meter.CreateCounter<long>("moodle_scorm_parses");
        durationMs = meter.CreateHistogram<double>("moodle_webservice_duration_ms");
        readGateWaitMs = meter.CreateHistogram<double>("moodle_read_gate_wait_ms");
        readGateActive = meter.CreateHistogram<long>("moodle_read_gate_active");
        readGateQueueDepth = meter.CreateHistogram<long>("moodle_read_gate_queue_depth");
        forumPostReadDiscussions = meter.CreateHistogram<long>("moodle_forum_post_read_discussions");
        forumPostReadCalls = meter.CreateHistogram<long>("moodle_forum_post_read_calls");
        forumPostReadFailures = meter.CreateHistogram<long>("moodle_forum_post_read_failures");
        forumPostReadMaxConcurrency = meter.CreateHistogram<long>("moodle_forum_post_read_max_concurrency");
        forumPostReadDurationMs = meter.CreateHistogram<double>("moodle_forum_post_read_duration_ms");
        scormParseContentEntryReads = meter.CreateHistogram<long>("moodle_scorm_content_entry_reads");
        scormParseSelectedUncompressedBytes = meter.CreateHistogram<long>("moodle_scorm_selected_uncompressed_bytes");
        scormParseAllocatedBytes = meter.CreateHistogram<long>("moodle_scorm_parse_allocated_bytes");
        scormParseDurationMs = meter.CreateHistogram<double>("moodle_scorm_parse_duration_ms");
        scormParseScoCount = meter.CreateHistogram<long>("moodle_scorm_parse_sco_count");
        scormParseFileCount = meter.CreateHistogram<long>("moodle_scorm_parse_file_count");
        requestPayloadBytes = meter.CreateHistogram<long>("moodle_webservice_request_payload_bytes");
        responsePayloadBytes = meter.CreateHistogram<long>("moodle_webservice_response_payload_bytes");
    }

    internal Meter InstrumentationMeter => meter;

    public void RecordMoodleWebServiceCall(string? connectionAlias = null) =>
        RecordMoodleWebServiceCall(connectionAlias, "unknown");

    public void RecordMoodleWebServiceCall(string? connectionAlias, string? functionName) =>
        RecordMoodleWebServiceCall(connectionAlias, functionName, "unknown");

    public void RecordMoodleWebServiceCall(string? connectionAlias, string? functionName, string? operationKind) =>
        calls.Add(1, Tags(connectionAlias, functionName, operationKind));

    public void RecordMoodleWebServiceCompleted(string? connectionAlias, string? functionName, double duration) =>
        RecordMoodleWebServiceCompleted(connectionAlias, functionName, "unknown", null, duration, 0, 0);

    public void RecordMoodleWebServiceCompleted(
        string? connectionAlias,
        string? functionName,
        string? operationKind,
        int? httpStatusCode,
        double duration,
        long requestBytes,
        long responseBytes)
    {
        var tags = Tags(connectionAlias, functionName, operationKind);
        AddHttpStatus(ref tags, httpStatusCode);
        durationMs.Record(Math.Max(0, duration), tags);
        requestPayloadBytes.Record(Math.Max(0, requestBytes), tags);
        responsePayloadBytes.Record(Math.Max(0, responseBytes), tags);
    }

    public void RecordMoodleWebServiceTransportAttempt(
        string? connectionAlias,
        string? functionName,
        string? operationKind,
        int attempt)
    {
        var safeAttempt = Math.Clamp(attempt, 1, 6);
        var tags = Tags(connectionAlias, functionName, operationKind);
        tags.Add("attempt", safeAttempt);
        transportAttempts.Add(1, tags);

        if (safeAttempt > 1)
        {
            var retryTags = Tags(connectionAlias, functionName, operationKind);
            retryTags.Add("retry_attempt", safeAttempt - 1);
            retries.Add(1, retryTags);
        }
    }

    public void RecordMoodleDiagnosticWrite(string outcome, int eventCount)
    {
        var tags = new TagList { { "outcome", NormalizeLabel(outcome, 16) } };
        diagnosticWriteEvents.Add(Math.Max(0, eventCount), tags);
    }

    public void RecordMoodleDiagnosticQueueDepth(int queuedGroups) =>
        diagnosticQueueDepth.Record(Math.Max(0, queuedGroups));

    public void RecordMoodleDiagnosticQueueWait(double waitDurationMs) =>
        diagnosticQueueWaitMs.Record(Math.Max(0, waitDurationMs));

    public void RecordMoodleDiagnosticCleanup(string outcome, int rowsDeleted)
    {
        var tags = new TagList { { "outcome", NormalizeLabel(outcome, 16) } };
        diagnosticCleanupRows.Add(Math.Max(0, rowsDeleted), tags);
    }

    public void RecordMoodleDiagnosticStdoutDrop(int eventCount) =>
        diagnosticStdoutDroppedEvents.Add(Math.Max(0, eventCount));

    public void RecordMoodleWebServiceLogicalRetry(
        string? connectionAlias,
        string? functionName,
        string? toolName,
        string? canonicalOperation,
        int attempt)
    {
        var tags = Tags(connectionAlias, functionName, "read");
        tags.Add("tool", NormalizeLabel(toolName, 80));
        tags.Add("canonical_operation", NormalizeLabel(canonicalOperation, 80));
        tags.Add("retry_attempt", Math.Clamp(attempt, 1, 2));
        logicalRetries.Add(1, tags);
    }

    public void RecordMoodleReadGateState(
        string? connectionAlias,
        string? functionName,
        string? toolName,
        string? canonicalOperation,
        string outcome,
        int active,
        int queued,
        double waitDurationMs)
    {
        var tags = Tags(connectionAlias, functionName, "read");
        tags.Add("tool", NormalizeLabel(toolName, 80));
        tags.Add("canonical_operation", NormalizeLabel(canonicalOperation, 80));
        tags.Add("outcome", NormalizeLabel(outcome, 32));
        readGateEvents.Add(1, tags);
        if (waitDurationMs > 0)
        {
            readGateWaitMs.Record(waitDurationMs, tags);
        }
        readGateActive.Record(Math.Max(0, active), tags);
        readGateQueueDepth.Record(Math.Max(0, queued), tags);
    }

    public void RecordForumPostReadBatch(
        string? toolName,
        string? canonicalOperation,
        int discussions,
        int postReadCalls,
        int failedDiscussions,
        int maxConcurrentPosts,
        double phaseDurationMs)
    {
        var tags = new TagList
        {
            { "tool", NormalizeLabel(toolName, 80) },
            { "canonical_operation", NormalizeLabel(canonicalOperation, 80) },
        };
        forumPostReadBatches.Add(1, tags);
        forumPostReadDiscussions.Record(Math.Max(0, discussions), tags);
        forumPostReadCalls.Record(Math.Max(0, postReadCalls), tags);
        forumPostReadFailures.Record(Math.Max(0, failedDiscussions), tags);
        forumPostReadMaxConcurrency.Record(Math.Max(0, maxConcurrentPosts), tags);
        forumPostReadDurationMs.Record(Math.Max(0, phaseDurationMs), tags);
    }

    public void RecordScormParse(
        string? toolName,
        string? canonicalOperation,
        int contentEntryReads,
        long selectedUncompressedBytes,
        long allocatedBytes,
        double parseDurationMs,
        int scoCount,
        int fileCount,
        bool succeeded)
    {
        var tags = new TagList
        {
            { "tool", NormalizeLabel(toolName, 80) },
            { "canonical_operation", NormalizeLabel(canonicalOperation, 80) },
            { "outcome", succeeded ? "success" : "failure" },
        };
        scormParses.Add(1, tags);
        scormParseContentEntryReads.Record(Math.Max(0, contentEntryReads), tags);
        scormParseSelectedUncompressedBytes.Record(Math.Max(0, selectedUncompressedBytes), tags);
        scormParseAllocatedBytes.Record(Math.Max(0, allocatedBytes), tags);
        scormParseDurationMs.Record(Math.Max(0, parseDurationMs), tags);
        scormParseScoCount.Record(Math.Max(0, scoCount), tags);
        scormParseFileCount.Record(Math.Max(0, fileCount), tags);
    }

    public void RecordMoodleWebServiceFailure(string? connectionAlias, string? functionName, string? errorCode, double duration)
        => RecordMoodleWebServiceFailure(connectionAlias, functionName, errorCode, duration, null);

    public void RecordMoodleWebServiceFailure(
        string? connectionAlias,
        string? functionName,
        string? errorCode,
        double duration,
        int? httpStatusCode)
        => RecordMoodleWebServiceFailure(
            connectionAlias,
            functionName,
            errorCode,
            "unknown",
            duration,
            httpStatusCode,
            0,
            0);

    public void RecordMoodleWebServiceFailure(
        string? connectionAlias,
        string? functionName,
        string? errorCode,
        string? operationKind,
        double duration,
        int? httpStatusCode,
        long requestBytes,
        long responseBytes)
    {
        var tags = Tags(connectionAlias, functionName, operationKind);
        tags.Add("error_code", MoodleErrorContract.NormalizeCode(errorCode));
        AddHttpStatus(ref tags, httpStatusCode);
        failures.Add(1, tags);
        durationMs.Record(Math.Max(0, duration), tags);
        requestPayloadBytes.Record(Math.Max(0, requestBytes), tags);
        responsePayloadBytes.Record(Math.Max(0, responseBytes), tags);
    }

    public void Dispose() => meter.Dispose();

    private static TagList Tags(string? connectionAlias, string? functionName, string? operationKind)
    {
        var tags = new TagList
        {
            { "connection", HashConnection(connectionAlias) },
            { "function", NormalizeFunction(functionName) },
            { "operation_kind", NormalizeOperationKind(operationKind) },
        };
        if (McpReadInvocationScope.IsActive)
        {
            tags.Add("tool", NormalizeLabel(McpReadInvocationScope.CurrentToolName, 80));
            tags.Add("canonical_operation", NormalizeLabel(McpReadInvocationScope.CurrentCanonicalOperation, 80));
        }

        return tags;
    }

    private static string HashConnection(string? connectionAlias)
    {
        if (string.IsNullOrWhiteSpace(connectionAlias))
        {
            return "unknown";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(connectionAlias));
        return "c_" + Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string NormalizeFunction(string? functionName)
        => MoodleCallDiagnosticCatalog.FunctionName(functionName);

    private static string NormalizeOperationKind(string? operationKind) => operationKind?.Trim().ToLowerInvariant() switch
    {
        "read" => "read",
        "write" => "write",
        _ => "other"
    };

    private static string NormalizeLabel(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= maxLength && normalized.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? normalized
            : "other";
    }

    private static void AddHttpStatus(ref TagList tags, int? httpStatusCode)
    {
        if (httpStatusCode is >= 100 and <= 599)
        {
            tags.Add("http_status", httpStatusCode.Value);
        }
    }
}
