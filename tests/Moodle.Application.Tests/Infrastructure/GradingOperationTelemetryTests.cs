using System.Diagnostics.Metrics;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class GradingOperationTelemetryTests
{
    [Fact]
    public void Package_read_and_outcome_metrics_include_low_cardinality_rollup_values()
    {
        var longMeasurements = new Dictionary<string, long>(StringComparer.Ordinal);
        var doubleMeasurements = new Dictionary<string, double>(StringComparer.Ordinal);
        using var telemetry = new GradingOperationTelemetry();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Moodle.Grading")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            longMeasurements[MeasurementKey(instrument.Name, tags)] = value);
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            doubleMeasurements[MeasurementKey(instrument.Name, tags)] = value);
        listener.Start();

        telemetry.RecordPackageSubmissionRead("hit", 12.5, 42, 16_384, gatewayCallCount: 0);
        telemetry.RecordPackageOutcome(
            "PartiallyReady", attemptCount: 1, retryReason: "http_429",
            artifactReadFailedCount: 2, contextUnavailableCount: 3);
        telemetry.RecordPhase(
            "grading_publication",
            "write",
            "http_429",
            18.5,
            itemCount: 1);

        Assert.Equal(1, longMeasurements["grading_package_submission_cache_operations|cache_result=hit"]);
        Assert.DoesNotContain(longMeasurements.Keys, key => key.StartsWith("grading_package_submission_read_calls|", StringComparison.Ordinal));
        Assert.Equal(42, longMeasurements["grading_package_submission_count|cache_result=hit"]);
        Assert.Equal(16_384, longMeasurements["grading_package_submission_response_bytes|cache_result=hit"]);
        Assert.Equal(1, longMeasurements["grading_package_outcomes|status=partially_ready"]);
        Assert.Equal(1, longMeasurements["grading_package_retries|reason=http_429"]);
        Assert.Equal(2, longMeasurements["grading_package_issue_count|code=artifact_read_failed"]);
        Assert.Equal(3, longMeasurements["grading_package_issue_count|code=context_unavailable"]);
        Assert.Equal(12.5, doubleMeasurements["grading_package_submission_read_duration_ms|cache_result=hit"]);
        Assert.Equal(1, longMeasurements[
            "grading_phase_operations|operation=grading_publication,phase=write,result=http_429"]);
        Assert.Equal(18.5, doubleMeasurements[
            "grading_phase_duration_ms|operation=grading_publication,phase=write,result=http_429"]);
        Assert.DoesNotContain(longMeasurements.Keys, key =>
            key.Contains("student", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("feedback", StringComparison.OrdinalIgnoreCase));
    }

    private static string MeasurementKey(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var tagValues = tags.ToArray()
            .OrderBy(tag => tag.Key, StringComparer.Ordinal)
            .Select(tag => $"{tag.Key}={tag.Value}");
        return $"{name}|{string.Join(',', tagValues)}";
    }
}
