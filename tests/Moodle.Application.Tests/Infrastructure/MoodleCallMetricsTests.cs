using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleCallMetricsTests
{
    [Fact]
    public void Failure_overload_preserves_legacy_telemetry_implementations()
    {
        var legacy = new LegacyTelemetry();
        IMoodleCallTelemetry telemetry = legacy;

        telemetry.RecordMoodleWebServiceFailure("campus", "mod_assign_get_submissions", "api_error", 42, 429);

        Assert.Equal(1, legacy.FailureCalls);
    }

    [Fact]
    public void Transport_retry_counter_distinguishes_initial_attempt_from_retries()
    {
        var measurements = new List<(string Name, Dictionary<string, object?> Tags)>();
        using var metrics = new MoodleCallMetrics();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.InstrumentationMeter))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        listener.Start();

        metrics.RecordMoodleWebServiceTransportAttempt("private alias", "core_course_get_contents", "read", 1);
        metrics.RecordMoodleWebServiceTransportAttempt("private alias", "core_course_get_contents", "read", 2);

        Assert.Equal(2, measurements.Count(item => item.Name == "moodle_webservice_transport_attempts"));
        var retry = Assert.Single(measurements, item => item.Name == "moodle_webservice_retries");
        Assert.Equal(1, retry.Tags["retry_attempt"]);
        Assert.DoesNotContain("private alias", string.Join('|', retry.Tags.Values));
    }

    [Fact]
    public void Failure_metrics_hash_connection_and_expose_only_safe_low_cardinality_tags()
    {
        var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var metrics = new MoodleCallMetrics();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.InstrumentationMeter))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, CopyTags(tags))));
        listener.Start();

        const string privateAlias = "Julio campus Moodle";
        metrics.RecordMoodleWebServiceFailure(
            privateAlias,
            "mod_assign_get_submissions",
            MoodleErrorContract.RequestTimeout,
            "read",
            1250,
            429,
            80,
            120);

        var failure = Assert.Single(measurements, item => item.Name == "moodle_webservice_failures");

        Assert.Equal("mod_assign_get_submissions", failure.Tags["function"]);
        Assert.NotEqual(privateAlias, failure.Tags["connection"]?.ToString());
        Assert.Matches("^c_[a-f0-9]{16}$", failure.Tags["connection"]?.ToString());
        Assert.Equal(MoodleErrorContract.RequestTimeout, failure.Tags["error_code"]);
        Assert.Equal("read", failure.Tags["operation_kind"]);
        Assert.Equal(429, failure.Tags["http_status"]);
        Assert.DoesNotContain(failure.Tags.Keys, key => key is "student_id" or "url" or "token" or "payload");
        Assert.DoesNotContain(privateAlias, string.Join('|', failure.Tags.Values));
        Assert.Equal(80, measurements.Single(item => item.Name == "moodle_webservice_request_payload_bytes").Value);
        Assert.Equal(120, measurements.Single(item => item.Name == "moodle_webservice_response_payload_bytes").Value);
    }

    [Fact]
    public void Moodle_metrics_keep_http_status_numeric_and_do_not_log_connection_aliases()
    {
        var logger = new CapturingLogger<GradingMetricsRollupService>();
        using var metrics = new MoodleCallMetrics();
        using var rollup = new GradingMetricsRollupService(
            logger,
            TimeProvider.System,
            moodleMeterFilter: metrics.InstrumentationMeter);
        const string secretAlias = "Julio campus Moodle";
        const string secretUrl = "https://moodle.example.test/webservice?token=synthetic-token";

        metrics.RecordMoodleWebServiceFailure(secretAlias, "mod_assign_get_submissions", "api_error", 321, 429);
        metrics.RecordMoodleWebServiceFailure(secretUrl, "core_course_get_contents", MoodleErrorContract.RequestTimeout, 1250, 504);
        var snapshot = rollup.FlushForTesting();

        Assert.Equal(1, snapshot.GetCount("moodle", "429"));
        Assert.Equal(1, snapshot.GetCount("moodle", "request_timeout"));
        Assert.Contains("HttpStatus=429", logger.Rendered);
        Assert.Contains("HttpStatus=504", logger.Rendered);
        Assert.DoesNotContain(secretAlias, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(secretUrl, logger.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", logger.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Forum_post_batch_metrics_record_counts_without_discussion_or_student_identifiers()
    {
        var measurements = new List<(string Name, long? LongValue, double? DoubleValue, Dictionary<string, object?> Tags)>();
        using var metrics = new MoodleCallMetrics();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.InstrumentationMeter))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, null, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, null, value, CopyTags(tags))));
        listener.Start();

        metrics.RecordForumPostReadBatch("read_forum", "forum.read", 25, 25, 1, 2, 123.5);

        Assert.Equal(6, measurements.Count);
        Assert.Equal(25, Assert.Single(measurements, item => item.Name == "moodle_forum_post_read_discussions").LongValue);
        Assert.Equal(25, Assert.Single(measurements, item => item.Name == "moodle_forum_post_read_calls").LongValue);
        Assert.Equal(1, Assert.Single(measurements, item => item.Name == "moodle_forum_post_read_failures").LongValue);
        Assert.Equal(2, Assert.Single(measurements, item => item.Name == "moodle_forum_post_read_max_concurrency").LongValue);
        Assert.Equal(123.5, Assert.Single(measurements, item => item.Name == "moodle_forum_post_read_duration_ms").DoubleValue);
        Assert.All(measurements, measurement =>
        {
            Assert.Equal(new[] { "canonical_operation", "tool" }, measurement.Tags.Keys.Order().ToArray());
            Assert.DoesNotContain("student-42", string.Join('|', measurement.Tags.Values));
            Assert.DoesNotContain("discussion-9001", string.Join('|', measurement.Tags.Values));
        });
    }

    [Fact]
    public void Forum_HTTP_429_metrics_are_attributed_to_the_active_read_tool()
    {
        var measurements = new List<(string Name, Dictionary<string, object?> Tags)>();
        using var metrics = new MoodleCallMetrics();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.InstrumentationMeter))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        listener.Start();

        using (new McpReadInvocationScope(metrics).Begin("read_forum", "forum.read"))
        {
            metrics.RecordMoodleWebServiceFailure(
                "private alias",
                "mod_forum_get_discussion_posts",
                "request_timeout",
                "read",
                100,
                429,
                64,
                128);
        }

        var failure = Assert.Single(measurements, item => item.Name == "moodle_webservice_failures");
        Assert.Equal("read_forum", failure.Tags["tool"]);
        Assert.Equal("forum.read", failure.Tags["canonical_operation"]);
        Assert.Equal(429, failure.Tags["http_status"]);
        Assert.DoesNotContain("private alias", string.Join('|', failure.Tags.Values));
    }

    [Fact]
    public void Scorm_parse_metrics_record_costs_with_only_low_cardinality_tool_tags()
    {
        var measurements = new List<(string Name, Dictionary<string, object?> Tags)>();
        using var metrics = new MoodleCallMetrics();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.InstrumentationMeter))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        listener.Start();

        metrics.RecordScormParse("ler_scorm", "scorm.read", 3, 4096, 8192, 12.5, 2, 3, succeeded: true);

        Assert.Equal(7, measurements.Count);
        Assert.All(measurements, measurement =>
        {
            Assert.Equal(new[] { "canonical_operation", "outcome", "tool" }, measurement.Tags.Keys.Order().ToArray());
            Assert.DoesNotContain("student-42", string.Join('|', measurement.Tags.Values));
            Assert.DoesNotContain("https://", string.Join('|', measurement.Tags.Values));
        });
    }

    private sealed class LegacyTelemetry : IMoodleCallTelemetry
    {
        public int FailureCalls { get; private set; }
        public void RecordMoodleWebServiceCall(string? connectionAlias = null) { }
        public void RecordMoodleWebServiceFailure(string? connectionAlias, string? functionName, string? errorCode, double durationMs) =>
            FailureCalls++;
    }

    private static Dictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> messages = [];

        public string Rendered => string.Join(Environment.NewLine, messages);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }
}
