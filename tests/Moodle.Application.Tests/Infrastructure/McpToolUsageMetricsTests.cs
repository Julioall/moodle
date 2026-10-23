using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class McpToolUsageMetricsTests
{
    [Fact]
    public void Records_low_cardinality_tool_tags_without_payloads()
    {
        var measurements = new ConcurrentBag<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Moodle.McpTools")
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(item => item.Key, item => item.Value))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(item => item.Key, item => item.Value))));
        listener.Start();

        using var metrics = new McpToolUsageMetrics();
        metrics.RecordInvocation(
            "spec0037_usage_probe",
            "spec0037.usage_probe",
            "spec0037_alias",
            "spec0037_metrics_test",
            "success",
            null,
            12.5);
        metrics.RecordInvocation(
            "not a valid tool with token=secret",
            "operation with payload",
            null,
            "spec0037_metrics_test",
            "denied",
            "permission denied",
            1);
        metrics.RecordTransportRateLimitRejection();

        var invocation = measurements.Single(item => item.Name == "mcp_tool_invocations" &&
            item.Tags["tool"]?.ToString() == "spec0037_usage_probe" &&
            item.Tags["exposure_profile"]?.ToString() == "spec0037_metrics_test");
        Assert.Equal("spec0037.usage_probe", invocation.Tags["canonical_operation"]);
        Assert.Equal("spec0037_alias", invocation.Tags["compatibility_alias_of"]);
        Assert.Equal("success", invocation.Tags["outcome"]);

        var sanitized = measurements.Single(item => item.Name == "mcp_tool_invocations" &&
            item.Tags["outcome"]?.ToString() == "denied" &&
            item.Tags["exposure_profile"]?.ToString() == "spec0037_metrics_test");
        Assert.Equal("other", sanitized.Tags["tool"]);
        Assert.Equal("other", sanitized.Tags["canonical_operation"]);
        Assert.DoesNotContain("secret", string.Join('|', sanitized.Tags.Values));
        Assert.Contains(measurements, item => item.Name == "mcp_tool_failures" && item.Tags["outcome"]?.ToString() == "denied");
        Assert.Contains(measurements, item => item.Name == "mcp_http_rate_limit_rejections" &&
            item.Tags["layer"]?.ToString() == "mcp_request_security" &&
            item.Tags["http_status"]?.ToString() == "429");
    }
}
