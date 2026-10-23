using Moodle.Infrastructure;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class GradingMetricsRollupServiceTests
{
    [Fact]
    public void Rollup_reports_fixed_bucket_p50_and_p95_by_minute()
    {
        var logger = new CapturingLogger<GradingMetricsRollupService>();
        using var metrics = new GradingOperationTelemetry();
        using var rollup = new GradingMetricsRollupService(
            logger,
            TimeProvider.System,
            gradingMeterFilter: metrics.InstrumentationMeter);

        metrics.RecordPhase("grading_batch", "fetch_submissions", "success", 10);
        metrics.RecordPhase("grading_batch", "fetch_submissions", "success", 100);
        metrics.RecordPhase("grading_batch", "fetch_submissions", "success", 1000);
        var snapshot = rollup.FlushForTesting();

        Assert.Equal(3, snapshot.GetCount("grading", "success"));
        Assert.Contains("Source=grading", logger.Rendered);
        Assert.Contains("Phase=fetch_submissions", logger.Rendered);
        Assert.Contains("P50Ms=100", logger.Rendered);
        Assert.Contains("P95Ms=1000", logger.Rendered);
    }

    [Fact]
    public void Rollup_includes_package_issue_codes_and_bounds_series_to_64()
    {
        var logger = new CapturingLogger<GradingMetricsRollupService>();
        using var metrics = new GradingOperationTelemetry();
        using var rollup = new GradingMetricsRollupService(
            logger,
            TimeProvider.System,
            gradingMeterFilter: metrics.InstrumentationMeter);

        metrics.RecordPackageOutcome("NeedsAttention", 2, "artifact_read_failed", 1, 0);
        metrics.RecordPackageOutcome("NeedsAttention", 2, "context_unavailable", 0, 1);
        for (var i = 0; i < 100; i++)
        {
            metrics.RecordPhase("synthetic_operation", $"phase_{i}", $"result_{i}", i + 1);
        }
        var snapshot = rollup.FlushForTesting();

        Assert.Equal(1, snapshot.GetCount("grading", "artifact_read_failed"));
        Assert.Equal(1, snapshot.GetCount("grading", "context_unavailable"));
        Assert.True(snapshot.SeriesCount <= 64);
        Assert.Contains("Result=artifact_read_failed", logger.Rendered);
        Assert.Contains("Result=context_unavailable", logger.Rendered);
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
            Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }
}
