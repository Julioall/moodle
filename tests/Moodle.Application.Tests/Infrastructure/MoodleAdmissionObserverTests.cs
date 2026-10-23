using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAdmissionObserverTests
{
    [Fact]
    public void Simulates_candidate_concurrency_and_records_only_bounded_dimensions()
    {
        var decisions = new ConcurrentQueue<(string Decision, string TrafficClass, string PartitionKind, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Moodle.Admission" && instrument.Name == "admission_candidate_decisions")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? decision = null;
            string? trafficClass = null;
            string? partitionKind = null;
            var allTags = new List<string>();
            foreach (var tag in tags)
            {
                var value = tag.Value?.ToString() ?? string.Empty;
                allTags.Add($"{tag.Key}={value}");
                if (tag.Key == "decision") decision = value;
                if (tag.Key == "traffic_class") trafficClass = value;
                if (tag.Key == "partition_kind") partitionKind = value;
            }
            decisions.Enqueue((decision ?? string.Empty, trafficClass ?? string.Empty, partitionKind ?? string.Empty, string.Join(';', allTags)));
        });
        listener.Start();

        using var observer = new MoodleAdmissionObserver(Options.Create(OptionsProfile(maxGlobal: 1)), TimeProvider.System);
        var context = Context();
        using var first = observer.Begin(context);
        using var queuedCandidate = observer.Begin(context);

        Assert.Equal(2, observer.ActiveGlobalForTesting);
        Assert.Equal(2, context.AttemptBudget.AttemptsUsed);
        Assert.Contains(decisions, item => item.Decision == "admitted");
        Assert.Contains(decisions, item => item.Decision == "would_queue");
        Assert.All(decisions, item =>
        {
            Assert.Equal("mcp", item.TrafficClass);
            Assert.Equal("connection", item.PartitionKind);
            Assert.DoesNotContain("owner-private", item.Tags, StringComparison.Ordinal);
            Assert.DoesNotContain("connection-private", item.Tags, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Applies_simulated_cooldown_and_bounds_partition_tracking_without_blocking()
    {
        var start = DateTimeOffset.UtcNow;
        using var clock = new ManualTimeProvider(start);
        using var observer = new MoodleAdmissionObserver(Options.Create(OptionsProfile(
            maxGlobal: 2,
            maximumConnections: 1)), clock);
        var firstContext = Context(now: start);
        observer.RecordRateLimit(firstContext, TimeSpan.FromSeconds(60));

        var cooling = observer.Begin(firstContext);
        Assert.Equal(1, observer.ActiveGlobalForTesting);
        cooling.Dispose();
        using var overflow = observer.Begin(Context(connection: "new-connection", now: start));

        Assert.Equal(0, observer.ActiveGlobalForTesting);
        Assert.Equal(1, observer.TrackedConnectionCountForTesting);

        clock.Advance(TimeSpan.FromSeconds(60));
        using var admitted = observer.Begin(firstContext);
        Assert.Equal(1, observer.ActiveGlobalForTesting);
    }

    [Fact]
    public void Records_separate_bounded_families_for_writes_and_downloads()
    {
        var families = new ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Moodle.Admission" && instrument.Name == "admission_candidate_decisions")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "family") families.Enqueue(tag.Value?.ToString() ?? string.Empty);
        });
        listener.Start();

        using var observer = new MoodleAdmissionObserver(Options.Create(OptionsProfile()), TimeProvider.System);
        using var write = observer.Begin(Context(operation: MoodleAdmissionOperation.Write));
        using var download = observer.Begin(Context(
            connection: "download-connection",
            operation: MoodleAdmissionOperation.Download));

        Assert.Contains("write", families);
        Assert.Contains("download", families);
    }

    private static MoodleAdmissionOptions OptionsProfile(int maxGlobal = 2, int maximumConnections = 10) => new()
    {
        Mode = MoodleAdmissionMode.Observe,
        MaxConcurrentGlobal = maxGlobal,
        MaxConcurrentPerInstallation = maxGlobal,
        MaxConcurrentPerConnection = 1,
        QueueLimitGlobal = 10,
        QueueLimitPerInstallation = 10,
        QueueLimitPerConnection = 10,
        QueueWaitTimeoutSeconds = 10,
        MaximumTrackedConnections = maximumConnections,
        MaximumTrackedInstallations = 5
    };

    private static MoodleAdmissionContext Context(
        string connection = "connection-private",
        DateTimeOffset? now = null,
        MoodleAdmissionOperation operation = MoodleAdmissionOperation.Read) =>
        new("owner-private", connection, "installation-private", MoodleAdmissionOrigin.Mcp,
            operation, (now ?? DateTimeOffset.UtcNow).AddMinutes(2), new MoodleAttemptBudget(3));
}
