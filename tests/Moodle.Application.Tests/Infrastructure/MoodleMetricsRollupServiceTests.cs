using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moodle.Infrastructure;
using Moodle.Presentation.Configuration;
using Moodle.Presentation.Security;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleMetricsRollupServiceTests
{
    [Fact]
    public void Rollup_reports_tool_and_function_counts_percentiles_status_and_payload_sizes_without_private_tags()
    {
        var logger = new CapturingLogger<MoodleMetricsRollupService>();
        using var moodle = new MoodleCallMetrics();
        using var tools = new McpToolUsageMetrics();
        using var rollup = new MoodleMetricsRollupService(
            logger,
            moodleMeterFilter: moodle.InstrumentationMeter,
            mcpMeterFilter: tools.InstrumentationMeter);

        const string privateAlias = "Secret learner connection";
        foreach (var duration in new[] { 10d, 100d, 1000d })
        {
            moodle.RecordMoodleWebServiceCall(privateAlias, "core_course_get_contents", "read");
            moodle.RecordMoodleWebServiceCompleted(
                privateAlias,
                "core_course_get_contents",
                "read",
                200,
                duration,
                40,
                (long)duration);
        }

        moodle.RecordMoodleWebServiceFailure(
            privateAlias,
            "core_course_get_contents",
            "api_error",
            30,
            429);
        tools.RecordInvocation(
            "get_course_module",
            "course.module.get",
            null,
            "Production",
            "success",
            null,
            125);

        var snapshot = rollup.FlushForTesting();

        Assert.Equal(3, snapshot.GetCount("moodle", "moodle_webservice_calls"));
        Assert.Equal(1, snapshot.GetCount("mcp", "mcp_tool_invocations"));
        Assert.Contains("Metric=moodle_webservice_duration_ms", logger.Rendered);
        Assert.Contains("P50Ms=100", logger.Rendered);
        Assert.Contains("P95Ms=1000", logger.Rendered);
        Assert.Contains("P99Ms=1000", logger.Rendered);
        Assert.Contains("Metric=moodle_webservice_response_payload_bytes", logger.Rendered);
        Assert.Contains("Metric=mcp_tool_duration_ms", logger.Rendered);
        Assert.Contains("Tool=get_course_module", logger.Rendered);
        Assert.Contains("HttpStatus=429", logger.Rendered);
        Assert.DoesNotContain(privateAlias, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("student_id", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.True(snapshot.SeriesCount <= MoodleMetricsRollupService.MaxSeries);
    }

    [Fact]
    public void Rollup_includes_forum_post_batch_metrics_with_only_tool_dimensions()
    {
        var logger = new CapturingLogger<MoodleMetricsRollupService>();
        using var moodle = new MoodleCallMetrics();
        using var rollup = new MoodleMetricsRollupService(
            logger,
            moodleMeterFilter: moodle.InstrumentationMeter);

        moodle.RecordForumPostReadBatch(
            "read_forum",
            "forum.read",
            discussions: 25,
            postReadCalls: 25,
            failedDiscussions: 1,
            maxConcurrentPosts: 2,
            phaseDurationMs: 123.5);

        var snapshot = rollup.FlushForTesting();

        Assert.Equal(1, snapshot.GetCount("moodle", "moodle_forum_post_read_batches"));
        Assert.Contains("Metric=moodle_forum_post_read_calls", logger.Rendered);
        Assert.Contains("Metric=moodle_forum_post_read_failures", logger.Rendered);
        Assert.Contains("Metric=moodle_forum_post_read_max_concurrency", logger.Rendered);
        Assert.Contains("Metric=moodle_forum_post_read_duration_ms", logger.Rendered);
        Assert.Contains("Tool=read_forum", logger.Rendered);
        Assert.Contains("CanonicalOperation=forum.read", logger.Rendered);
        Assert.DoesNotContain("discussion-", logger.Rendered, StringComparison.Ordinal);
        Assert.True(snapshot.SeriesCount <= MoodleMetricsRollupService.MaxSeries);
    }

    [Fact]
    public void Rollup_bounds_series_and_summarizes_read_gate_wait_without_private_tags()
    {
        var logger = new CapturingLogger<MoodleMetricsRollupService>();
        using var moodle = new MoodleCallMetrics();
        using var rollup = new MoodleMetricsRollupService(logger, moodleMeterFilter: moodle.InstrumentationMeter);
        for (var index = 0; index < 200; index++)
        {
            moodle.RecordMoodleReadGateState(
                "private alias",
                "core_course_get_contents",
                "get_course_module",
                "course.module.get",
                "queued",
                2,
                1,
                (index + 1) * 10);
        }

        for (var index = 0; index < 200; index++)
        {
            moodle.RecordMoodleWebServiceCall($"private alias {index}", $"core_course_function_{index}", "read");
        }

        var snapshot = rollup.FlushForTesting();

        Assert.True(snapshot.SeriesCount <= MoodleMetricsRollupService.MaxSeries);
        Assert.Contains("Metric=moodle_read_gate_events", logger.Rendered);
        Assert.Contains("Metric=moodle_read_gate_wait_ms", logger.Rendered);
        Assert.Contains("P50Ms=1000", logger.Rendered);
        Assert.DoesNotContain("private alias", logger.Rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rollup_includes_snapshot_hits_misses_and_queue_wait_with_safe_dataset_stage_dimensions()
    {
        var logger = new CapturingLogger<MoodleMetricsRollupService>();
        using var snapshots = new MoodleSnapshotMetrics();
        using var rollup = new MoodleMetricsRollupService(
            logger,
            snapshotMeterFilter: snapshots.InstrumentationMeter);

        snapshots.RecordL1Hit("courses");
        snapshots.RecordL2Hit("activities", DateTimeOffset.UtcNow.AddSeconds(-4));
        snapshots.RecordMiss("students");
        snapshots.RecordQueueWait("courses", 125);
        snapshots.RecordCourseOverviewResponse(
            47.5,
            1024,
            "ok",
            "partial",
            "stale",
            "queued",
            "not_authorized",
            "unavailable",
            "not_requested");
        snapshots.RecordCourseOverviewResponse(
            20,
            2048,
            "unexpected user value",
            "private learner name",
            "fresh",
            "not_needed",
            "complete",
            "expired",
            "deferred");

        var snapshot = rollup.FlushForTesting();

        Assert.Equal(1, snapshot.GetCount("snapshot", "moodle_snapshot_l1_hits"));
        Assert.Equal(1, snapshot.GetCount("snapshot", "moodle_snapshot_l2_hits"));
        Assert.Equal(1, snapshot.GetCount("snapshot", "moodle_snapshot_misses"));
        Assert.Contains("Dataset=courses", logger.Rendered);
        Assert.Contains("Stage=queue_wait", logger.Rendered);
        Assert.Contains("Metric=moodle_snapshot_queue_wait_ms", logger.Rendered);
        Assert.Contains("P50Ms=250", logger.Rendered);
        Assert.Contains("Metric=moodle_course_overview_duration_ms", logger.Rendered);
        Assert.Contains("Metric=moodle_course_overview_response_payload_bytes", logger.Rendered);
        Assert.Contains("Metric=moodle_course_overview_observations", logger.Rendered);
        Assert.Contains("Dataset=students", logger.Rendered);
        Assert.Contains("Stage=coverage", logger.Rendered);
        Assert.Contains("Outcome=partial", logger.Rendered);
        Assert.Contains("Scope=course_overview", logger.Rendered);
        Assert.DoesNotContain("private learner name", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unexpected user value", logger.Rendered, StringComparison.OrdinalIgnoreCase);
        Assert.True(snapshot.SeriesCount <= MoodleMetricsRollupService.MaxSeries);
    }

    [Fact]
    public void Rollup_exports_admission_decisions_with_only_bounded_dimensions()
    {
        var logger = new CapturingLogger<MoodleMetricsRollupService>();
        using var observer = new IngressAdmissionObserver(
            Options.Create(new AdmissionRolloutOptions { LocalReadBurst = 1, MaximumTrackedPartitions = 1 }),
            TimeProvider.System);
        using var rollup = new MoodleMetricsRollupService(
            logger,
            admissionMeterFilter: observer.InstrumentationMeter);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.91");
        var overflowContext = new DefaultHttpContext();
        overflowContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.92");
        var metadata = new AdmissionRouteMetadata(AdmissionTrafficClass.LocalRead, "course_catalog");

        Assert.True(observer.Evaluate(context, metadata).IsAllowed);
        Assert.True(observer.Evaluate(overflowContext, metadata).IsAllowed);
        Assert.False(observer.Evaluate(overflowContext, metadata).IsAllowed);

        var snapshot = rollup.FlushForTesting();
        Assert.Equal(3, snapshot.GetCount("admission", "admission_candidate_decisions"));
        Assert.Equal(2, snapshot.GetCount("admission", "admission_candidate_partition_overflows"));
        Assert.Contains("TrafficClass=local_read", logger.Rendered);
        Assert.Contains("Family=course_catalog", logger.Rendered);
        Assert.Contains("Decision=would_reject", logger.Rendered);
        Assert.Contains("PartitionKind=overflow", logger.Rendered);
        Assert.DoesNotContain("192.0.2.91", logger.Rendered, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly List<string> messages = [];
        public string Rendered => string.Join(Environment.NewLine, messages);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Add(formatter(state, exception));
    }
}
