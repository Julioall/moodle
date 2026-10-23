using System.Diagnostics.Metrics;

namespace Moodle.Infrastructure;

public sealed class MoodleSnapshotMetrics : IDisposable
{
    private readonly Meter meter = new("Moodle.Snapshots", "1.0.0");
    private readonly Counter<long> l1Hits;
    private readonly Counter<long> l2Hits;
    private readonly Counter<long> misses;
    private readonly Counter<long> refreshes;
    private readonly Histogram<double> snapshotAgeSeconds;
    private readonly Histogram<double> syncDurationMs;
    private readonly Counter<long> queueEnqueued;
    private readonly Counter<long> queueRejected;
    private readonly Counter<long> throttleWaits;
    private readonly Counter<long> runsCleaned;
    private readonly Histogram<long> payloadBytes;
    private readonly Counter<long> gradebookReads;
    private readonly Counter<long> gradebookFallbacks;
    private readonly Counter<long> gradebookCacheHits;
    private readonly Histogram<double> identityResolutionDurationMs;
    private readonly Counter<long> identityResolutionCommands;
    private readonly Histogram<double> snapshotReadDurationMs;
    private readonly Counter<long> snapshotReadCommands;
    private readonly Histogram<double> queueWaitMs;
    private readonly Histogram<double> leaseClaimDurationMs;
    private readonly Counter<long> leaseClaimCommands;
    private readonly Histogram<double> transactionDurationMs;
    private readonly Counter<long> transactionCommands;
    private readonly Histogram<double> publishDurationMs;
    private readonly Counter<long> publishCommands;
    private readonly Histogram<double> courseOverviewDurationMs;
    private readonly Histogram<long> courseOverviewResponsePayloadBytes;
    private readonly Counter<long> courseOverviewObservations;

    public MoodleSnapshotMetrics()
    {
        l1Hits = meter.CreateCounter<long>("moodle_snapshot_l1_hits");
        l2Hits = meter.CreateCounter<long>("moodle_snapshot_l2_hits");
        misses = meter.CreateCounter<long>("moodle_snapshot_misses");
        refreshes = meter.CreateCounter<long>("moodle_snapshot_refreshes");
        snapshotAgeSeconds = meter.CreateHistogram<double>("moodle_snapshot_age_seconds");
        syncDurationMs = meter.CreateHistogram<double>("moodle_snapshot_sync_duration_ms");
        queueEnqueued = meter.CreateCounter<long>("moodle_snapshot_queue_enqueued");
        queueRejected = meter.CreateCounter<long>("moodle_snapshot_queue_rejected");
        throttleWaits = meter.CreateCounter<long>("moodle_snapshot_throttle_waits");
        runsCleaned = meter.CreateCounter<long>("moodle_snapshot_runs_cleaned");
        payloadBytes = meter.CreateHistogram<long>("moodle_snapshot_payload_bytes");
        gradebookReads = meter.CreateCounter<long>("moodle_gradebook_reads");
        gradebookFallbacks = meter.CreateCounter<long>("moodle_gradebook_fallbacks");
        gradebookCacheHits = meter.CreateCounter<long>("moodle_gradebook_cache_hits");
        identityResolutionDurationMs = meter.CreateHistogram<double>("moodle_snapshot_identity_resolution_duration_ms");
        identityResolutionCommands = meter.CreateCounter<long>("moodle_snapshot_identity_resolution_commands");
        snapshotReadDurationMs = meter.CreateHistogram<double>("moodle_snapshot_database_read_duration_ms");
        snapshotReadCommands = meter.CreateCounter<long>("moodle_snapshot_database_read_commands");
        queueWaitMs = meter.CreateHistogram<double>("moodle_snapshot_queue_wait_ms");
        leaseClaimDurationMs = meter.CreateHistogram<double>("moodle_snapshot_lease_claim_duration_ms");
        leaseClaimCommands = meter.CreateCounter<long>("moodle_snapshot_lease_claim_commands");
        transactionDurationMs = meter.CreateHistogram<double>("moodle_snapshot_transaction_duration_ms");
        transactionCommands = meter.CreateCounter<long>("moodle_snapshot_transaction_commands");
        publishDurationMs = meter.CreateHistogram<double>("moodle_snapshot_publish_duration_ms");
        publishCommands = meter.CreateCounter<long>("moodle_snapshot_publish_commands");
        courseOverviewDurationMs = meter.CreateHistogram<double>("moodle_course_overview_duration_ms");
        courseOverviewResponsePayloadBytes = meter.CreateHistogram<long>("moodle_course_overview_response_payload_bytes");
        courseOverviewObservations = meter.CreateCounter<long>("moodle_course_overview_observations");
    }

    internal Meter InstrumentationMeter => meter;

    public void RecordL1Hit(string dataset) => l1Hits.Add(1, new KeyValuePair<string, object?>("dataset", dataset));

    public void RecordL2Hit(string dataset, DateTimeOffset updatedAt)
    {
        l2Hits.Add(1, new KeyValuePair<string, object?>("dataset", dataset));
        snapshotAgeSeconds.Record(Math.Max(0, (DateTimeOffset.UtcNow - updatedAt).TotalSeconds), new KeyValuePair<string, object?>("dataset", dataset));
    }

    public void RecordMiss(string dataset) => misses.Add(1, new KeyValuePair<string, object?>("dataset", dataset));
    public void RecordRefresh(string dataset) => refreshes.Add(1, new KeyValuePair<string, object?>("dataset", dataset));
    public void RecordSyncDuration(string dataset, double durationMs) => syncDurationMs.Record(durationMs, new KeyValuePair<string, object?>("dataset", dataset));
    public void RecordQueueEnqueued(string dataset) => queueEnqueued.Add(1, new KeyValuePair<string, object?>("dataset", dataset));
    public void RecordQueueRejected(string dataset) => queueRejected.Add(1, new KeyValuePair<string, object?>("dataset", dataset));
    public void RecordThrottleWait(string scope) => throttleWaits.Add(1, new KeyValuePair<string, object?>("scope", scope));
    public void RecordRunsCleaned(int count) => runsCleaned.Add(count);
    public void RecordPayloadBytes(string dataset, long bytes) => payloadBytes.Record(bytes, new KeyValuePair<string, object?>("dataset", dataset));
    public void RecordGradebookRead(string mode) => gradebookReads.Add(1, new KeyValuePair<string, object?>("mode", mode));
    public void RecordGradebookFallback(string reason) => gradebookFallbacks.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void RecordGradebookCacheHit(string mode) => gradebookCacheHits.Add(1, new KeyValuePair<string, object?>("mode", mode));
    public void RecordIdentityResolution(string dataset, double durationMs, long commands) =>
        RecordStage(identityResolutionDurationMs, identityResolutionCommands, dataset, "connection", "identity_resolution", durationMs, commands);
    public void RecordSnapshotRead(string dataset, double durationMs, long commands) =>
        RecordStage(snapshotReadDurationMs, snapshotReadCommands, dataset, "snapshot", "head_read", durationMs, commands);
    public void RecordQueueWait(string dataset, double durationMs) =>
        queueWaitMs.Record(NonNegative(durationMs), StageTags(dataset, "sync", "queue_wait"));
    public void RecordLeaseClaim(string dataset, double durationMs, long commands) =>
        RecordStage(leaseClaimDurationMs, leaseClaimCommands, dataset, "sync", "lease_claim", durationMs, commands);
    public void RecordTransaction(string dataset, double durationMs, long commands) =>
        RecordStage(transactionDurationMs, transactionCommands, dataset, "sync", "transaction", durationMs, commands);
    public void RecordPublish(string dataset, double durationMs, long commands) =>
        RecordStage(publishDurationMs, publishCommands, dataset, "sync", "publish", durationMs, commands);

    public void RecordCourseOverviewResponse(
        double durationMs,
        long responseBytes,
        string outcome,
        string studentsCoverage,
        string studentsFreshness,
        string studentsRefresh,
        string gradebookCoverage,
        string gradebookFreshness,
        string gradebookRefresh)
    {
        KeyValuePair<string, object?>[] responseTags =
        [
            new("dataset", "course_overview"),
            new("scope", "portal"),
            new("stage", "response"),
            new("outcome", NormalizeOverviewOutcome(outcome)),
        ];
        courseOverviewDurationMs.Record(NonNegative(durationMs), responseTags);
        courseOverviewResponsePayloadBytes.Record(Math.Max(0, responseBytes), responseTags);

        RecordCourseOverviewState("students", "coverage", studentsCoverage);
        RecordCourseOverviewState("students", "freshness", studentsFreshness);
        RecordCourseOverviewState("students", "refresh", studentsRefresh);
        RecordCourseOverviewState("gradebook", "coverage", gradebookCoverage);
        RecordCourseOverviewState("gradebook", "freshness", gradebookFreshness);
        RecordCourseOverviewState("gradebook", "refresh", gradebookRefresh);
    }

    private void RecordCourseOverviewState(string dataset, string stage, string state)
    {
        var tags = StageTags(dataset, "course_overview", stage)
            .Append(new KeyValuePair<string, object?>("outcome", NormalizeOverviewState(stage, state)))
            .ToArray();
        courseOverviewObservations.Add(1, tags);
    }

    private static string NormalizeOverviewOutcome(string outcome) => outcome is "ok" or "response_budget_exceeded"
        ? outcome
        : "other";

    private static string NormalizeOverviewState(string stage, string state) => (stage, state) switch
    {
        ("coverage", "complete" or "partial" or "unavailable" or "not_authorized") => state,
        ("freshness", "fresh" or "stale" or "expired" or "unavailable") => state,
        ("refresh", "not_needed" or "queued" or "deferred" or "not_requested") => state,
        _ => "other",
    };

    private static KeyValuePair<string, object?>[] StageTags(string dataset, string scope, string stage) =>
    [
        new("dataset", dataset),
        new("scope", scope),
        new("stage", stage),
    ];

    private static double NonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static void RecordStage(
        Histogram<double> duration,
        Counter<long> commandsCounter,
        string dataset,
        string scope,
        string stage,
        double durationMs,
        long commands)
    {
        var tags = StageTags(dataset, scope, stage);
        duration.Record(NonNegative(durationMs), tags);
        if (commands > 0)
        {
            commandsCounter.Add(commands, tags);
        }
    }

    public void Dispose() => meter.Dispose();
}
