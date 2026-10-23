using System.Diagnostics;
using System.Diagnostics.Metrics;
using Moodle.Domain.Reports;

namespace Moodle.Application.Reports.Templates;

// Tags are closed vocabularies. Never include owner, connection, course/student
// identities, definitions, free text, category paths or exception messages.
public static class ReportMetrics
{
    public const string MeterName = "Moodle.Reports";
    private static readonly Meter Meter = new(MeterName, "1.0");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("reports.operations");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("reports.duration", "s");
    private static readonly Histogram<long> Courses = Meter.CreateHistogram<long>("reports.selection.courses", "{course}");
    private static readonly Histogram<long> Rows = Meter.CreateHistogram<long>("reports.rows", "{row}");
    private static readonly Histogram<long> Bytes = Meter.CreateHistogram<long>("reports.artifact.bytes", "By");
    private static readonly Counter<long> Unavailable = Meter.CreateCounter<long>("reports.unavailable", "{coverage}");
    private static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>("reports.queue.wait", "s");
    private static readonly Counter<long> Enqueued = Meter.CreateCounter<long>("reports.queue.enqueued", "{job}");
    public enum Stage { Preview, Selection, Processing, Render, Storage }
    public enum Result { Success, Partial, Failed, Cancelled, Busy, Timeout, Limit, LeaseLost }
    public static Measurement Begin(Stage stage) => new(stage);
    public static void Selection(int count) => Courses.Record(count);
    public static void Artifact(int count) => Bytes.Record(count);
    public static void Queued() => Enqueued.Add(1);
    public static void Claimed(DateTimeOffset requestedAt) => QueueWait.Record(Math.Max(0, (DateTimeOffset.UtcNow - requestedAt).TotalSeconds));
    public static void Dataset(ReportTable table)
    {
        Rows.Record(table.Rows.Count);
        foreach (var item in table.Coverage.Where(item => item.Source is "unavailable" or "incomplete").DistinctBy(item => (item.CourseId, item.Family)))
        {
            var family = item.Family is "course" or "student" or "population" or "participants" or "access" or "enrollment" or "gradebook" or "grade_scale" ? item.Family : "other";
            Unavailable.Add(1, new KeyValuePair<string, object?>("family", family));
        }
    }
    public sealed class Measurement(Stage stage) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private int disposed;
        public Result Outcome { get; set; } = Result.Failed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            TagList tags = new() { { "stage", stage.ToString().ToLowerInvariant() }, { "result", Outcome.ToString().ToLowerInvariant() } };
            Operations.Add(1, tags);
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }
}
