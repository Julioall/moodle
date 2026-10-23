using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Moodle.Application.Reports.Templates;
using Moodle.Domain.Reports;

namespace Moodle.Application.Tests.Reports.Templates;

public sealed class ReportMetricsTests
{
    [Fact]
    public void MeasurementsUseClosedTagsAndNeverIncludeDatasetIdentitiesOrFreeText()
    {
        var measurements = new ConcurrentBag<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, target) => { if (instrument.Meter.Name == ReportMetrics.MeterName) target.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();
        using (var measurement = ReportMetrics.Begin(ReportMetrics.Stage.Selection))
        { measurement.Outcome = ReportMetrics.Result.Success; ReportMetrics.Selection(2); }
        var at = DateTimeOffset.UtcNow;
        ReportMetrics.Dataset(new([], [], [new("private-course", "free-text-family", ["private-field"], "unavailable", null, null, false, "private-reason")], ["private-warning"], at, at, at));
        ReportMetrics.Artifact(100);
        ReportMetrics.Queued();
        ReportMetrics.Claimed(at);
        Assert.Contains(measurements, item => item.Name == "reports.operations" && item.Tags.Any(tag => tag.Key == "stage" && Equals(tag.Value, "selection")));
        Assert.Contains(measurements, item => item.Name == "reports.duration");
        Assert.Contains(measurements, item => item.Name == "reports.unavailable" && item.Tags.Single().Value!.Equals("other"));
        Assert.Contains(measurements, item => item.Name == "reports.artifact.bytes");
        Assert.Contains(measurements, item => item.Name == "reports.queue.wait");
        Assert.Contains(measurements, item => item.Name == "reports.queue.enqueued");
        Assert.All(measurements, item => Assert.All(item.Tags, tag =>
        {
            Assert.Contains(tag.Key, new[] { "stage", "result", "family" });
            Assert.DoesNotContain("private", tag.Value?.ToString() ?? "");
            Assert.DoesNotContain("free-text", tag.Value?.ToString() ?? "");
        }));
    }
}
