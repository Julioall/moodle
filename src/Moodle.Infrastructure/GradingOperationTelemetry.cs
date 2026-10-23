using System.Diagnostics;
using System.Diagnostics.Metrics;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

public sealed class GradingOperationTelemetry : IGradingOperationTelemetry, IDisposable
{
    private readonly Meter meter = new("Moodle.Grading", "1.0.0");
    private readonly Histogram<double> duration;
    private readonly Counter<long> operations;
    private readonly Histogram<long> queries;
    private readonly Histogram<long> items;
    private readonly Histogram<long> bytesProcessed;
    private readonly Counter<long> resourceRegisterCount;
    private readonly Counter<long> resourceReadCount;
    private readonly Histogram<double> resourceReadDuration;
    private readonly Histogram<double> resourceDownloadDuration;
    private readonly Histogram<long> resourceDownloadBytes;
    private readonly Counter<long> resourceCacheHit;
    private readonly Counter<long> resourceCacheMiss;
    private readonly Counter<long> resourceReadFailure;
    private readonly Counter<long> legacyFallbackCount;
    private readonly Counter<long> packageSubmissionReadCalls;
    private readonly Counter<long> packageSubmissionCacheOperations;
    private readonly Histogram<double> packageSubmissionReadDuration;
    private readonly Histogram<long> packageSubmissionCount;
    private readonly Histogram<long> packageSubmissionResponseBytes;
    private readonly Counter<long> packageOutcomeCount;
    private readonly Histogram<long> packageAttemptCount;
    private readonly Counter<long> packageRetryCount;
    private readonly Counter<long> packageIssueCount;

    public GradingOperationTelemetry()
    {
        duration = meter.CreateHistogram<double>("grading_phase_duration_ms");
        operations = meter.CreateCounter<long>("grading_phase_operations");
        queries = meter.CreateHistogram<long>("grading_phase_sql_queries");
        items = meter.CreateHistogram<long>("grading_phase_items");
        bytesProcessed = meter.CreateHistogram<long>("grading_phase_bytes");
        resourceRegisterCount = meter.CreateCounter<long>("resource_register_count");
        resourceReadCount = meter.CreateCounter<long>("resource_read_count");
        resourceReadDuration = meter.CreateHistogram<double>("resource_read_duration_ms");
        resourceDownloadDuration = meter.CreateHistogram<double>("resource_download_duration_ms");
        resourceDownloadBytes = meter.CreateHistogram<long>("resource_download_bytes");
        resourceCacheHit = meter.CreateCounter<long>("resource_cache_hit");
        resourceCacheMiss = meter.CreateCounter<long>("resource_cache_miss");
        resourceReadFailure = meter.CreateCounter<long>("resource_read_failure");
        legacyFallbackCount = meter.CreateCounter<long>("legacy_fallback_count");
        packageSubmissionReadCalls = meter.CreateCounter<long>("grading_package_submission_read_calls");
        packageSubmissionCacheOperations = meter.CreateCounter<long>("grading_package_submission_cache_operations");
        packageSubmissionReadDuration = meter.CreateHistogram<double>("grading_package_submission_read_duration_ms");
        packageSubmissionCount = meter.CreateHistogram<long>("grading_package_submission_count");
        packageSubmissionResponseBytes = meter.CreateHistogram<long>("grading_package_submission_response_bytes");
        packageOutcomeCount = meter.CreateCounter<long>("grading_package_outcomes");
        packageAttemptCount = meter.CreateHistogram<long>("grading_package_attempt_count");
        packageRetryCount = meter.CreateCounter<long>("grading_package_retries");
        packageIssueCount = meter.CreateCounter<long>("grading_package_issue_count");
    }

    internal Meter InstrumentationMeter => meter;

    public void RecordPhase(string operation, string phase, string result, double durationMs, int queryCount = 0, int itemCount = 0, long bytes = 0)
    {
        var tags = new TagList
        {
            { "operation", Normalize(operation) },
            { "phase", Normalize(phase) },
            { "result", Normalize(result) }
        };
        operations.Add(1, tags);
        duration.Record(Math.Max(0, durationMs), tags);
        queries.Record(Math.Max(0, queryCount), tags);
        items.Record(Math.Max(0, itemCount), tags);
        bytesProcessed.Record(Math.Max(0, bytes), tags);

        if (string.Equals(phase, "legacy_fallback", StringComparison.Ordinal))
        {
            legacyFallbackCount.Add(1, new TagList { { "reason", Normalize(result) } });
        }

        if (!string.Equals(operation, "moodle_resource", StringComparison.Ordinal))
        {
            return;
        }

        var resourceTags = new TagList { { "result", Normalize(result) } };
        if (string.Equals(phase, "register", StringComparison.Ordinal))
        {
            resourceRegisterCount.Add(1, resourceTags);
            return;
        }

        if (!string.Equals(phase, "read", StringComparison.Ordinal))
        {
            return;
        }

        resourceReadCount.Add(1, resourceTags);
        resourceReadDuration.Record(Math.Max(0, durationMs), resourceTags);
        if (string.Equals(result, "cache_hit", StringComparison.Ordinal))
        {
            resourceCacheHit.Add(1, resourceTags);
            return;
        }

        if (string.Equals(result, "success", StringComparison.Ordinal))
        {
            resourceCacheMiss.Add(1, resourceTags);
            resourceDownloadDuration.Record(Math.Max(0, durationMs), resourceTags);
            resourceDownloadBytes.Record(Math.Max(0, bytes), resourceTags);
            return;
        }

        resourceReadFailure.Add(1, resourceTags);
    }

    public void RecordPackageSubmissionRead(
        string cacheResult,
        double durationMs,
        int submissionCount,
        long estimatedBytes,
        int gatewayCallCount)
    {
        var normalizedResult = NormalizeCacheResult(cacheResult);
        var tags = new TagList { { "cache_result", normalizedResult } };
        packageSubmissionCacheOperations.Add(1, tags);
        if (gatewayCallCount > 0)
        {
            packageSubmissionReadCalls.Add(gatewayCallCount, tags);
        }
        packageSubmissionReadDuration.Record(Math.Max(0, durationMs), tags);
        packageSubmissionCount.Record(Math.Max(0, submissionCount), tags);
        packageSubmissionResponseBytes.Record(Math.Max(0, estimatedBytes), tags);
        RecordPhase("grading_package", "submission_read", normalizedResult, durationMs,
            queryCount: gatewayCallCount, itemCount: submissionCount, bytes: estimatedBytes);
    }

    public void RecordPackageOutcome(
        string status,
        int attemptCount,
        string? retryReason,
        int artifactReadFailedCount,
        int contextUnavailableCount)
    {
        var normalizedStatus = NormalizePackageStatus(status);
        var statusTags = new TagList { { "status", normalizedStatus } };
        packageOutcomeCount.Add(1, statusTags);
        packageAttemptCount.Record(Math.Max(0, attemptCount), statusTags);
        if (!string.IsNullOrWhiteSpace(retryReason) && retryReason != "package_failure")
        {
            packageRetryCount.Add(1, new TagList { { "reason", NormalizeRetryReason(retryReason) } });
        }
        if (artifactReadFailedCount > 0)
        {
            packageIssueCount.Add(artifactReadFailedCount,
                new TagList { { "code", "artifact_read_failed" } });
        }
        if (contextUnavailableCount > 0)
        {
            packageIssueCount.Add(contextUnavailableCount,
                new TagList { { "code", "context_unavailable" } });
        }
    }

    public void Dispose() => meter.Dispose();

    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim()[..Math.Min(64, value.Trim().Length)];

    private static string NormalizeCacheResult(string value) => value.Trim().ToLowerInvariant() switch
    {
        "hit" => "hit",
        "miss" => "miss",
        "bypass" => "bypass",
        _ => "unknown"
    };

    private static string NormalizePackageStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ready" => "ready",
        "partiallyready" => "partially_ready",
        "resultsreceived" => "results_received",
        "preparing" => "preparing",
        "needsattention" => "needs_attention",
        "cancelled" => "cancelled",
        _ => "unknown"
    };

    private static string NormalizeRetryReason(string value) => value.Trim().ToLowerInvariant() switch
    {
        "http_429" => "http_429",
        "timeout" => "timeout",
        "artifact_read_failed" => "artifact_read_failed",
        "context_unavailable" => "context_unavailable",
        "transient_dependency" => "transient_dependency",
        "finalization_conflict" => "finalization_conflict",
        "lease_lost" => "lease_lost",
        "package_failure" => "package_failure",
        _ => "other"
    };
}
