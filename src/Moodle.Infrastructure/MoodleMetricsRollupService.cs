using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure;

/// <summary>
/// Exports bounded one-minute summaries from Moodle and MCP tool meters. Only
/// allowlisted metric dimensions are retained; arguments and payload content
/// are never observed.
/// </summary>
internal sealed class MoodleMetricsRollupService : BackgroundService, IDisposable
{
    internal const int MaxSeries = 128;
    private const string Other = "other";
    private static readonly double[] Buckets =
    [
        1, 10, 50, 100, 250, 500, 1000, 2000, 5000, 10000, 30000, 60000, double.PositiveInfinity
    ];

    private readonly object sync = new();
    private readonly Dictionary<SeriesKey, Aggregate> series = [];
    private readonly ILogger<MoodleMetricsRollupService> logger;
    private readonly Meter? moodleMeterFilter;
    private readonly Meter? mcpMeterFilter;
    private readonly Meter? snapshotMeterFilter;
    private readonly Meter? admissionMeterFilter;
    private readonly MeterListener listener;
    private readonly List<double> processCpuSamples = [];
    private readonly List<double> workingSetSamples = [];
    private TimeSpan? previousProcessCpu;
    private long? previousProcessSampleTimestamp;
    private bool disposed;

    public MoodleMetricsRollupService(
        ILogger<MoodleMetricsRollupService> logger,
        Meter? moodleMeterFilter = null,
        Meter? mcpMeterFilter = null,
        Meter? snapshotMeterFilter = null,
        Meter? admissionMeterFilter = null)
    {
        this.logger = logger;
        this.moodleMeterFilter = moodleMeterFilter;
        this.mcpMeterFilter = mcpMeterFilter;
        this.snapshotMeterFilter = snapshotMeterFilter;
        this.admissionMeterFilter = admissionMeterFilter;
        listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (IsSupported(instrument) && IsIncludedMeter(instrument.Meter))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        listener.Start();
    }

    internal int SeriesCount
    {
        get
        {
            lock (sync)
            {
                return series.Count;
            }
        }
    }

    internal RollupSnapshot FlushForTesting() => FlushSnapshot();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextWindow = DateTimeOffset.UtcNow.AddMinutes(1);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            RecordProcessResources();
            if (DateTimeOffset.UtcNow >= nextWindow)
            {
                FlushSnapshot();
                FlushProcessResources();
                nextWindow = DateTimeOffset.UtcNow.AddMinutes(1);
            }
        }
    }

    public override void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            listener.Dispose();
        }

        base.Dispose();
    }

    private static bool IsSupported(Instrument instrument) => instrument.Meter.Name switch
    {
        "Moodle.MoodleCalls" => instrument.Name is
            "moodle_webservice_calls" or "moodle_webservice_failures" or "moodle_webservice_duration_ms" or
            "moodle_webservice_request_payload_bytes" or "moodle_webservice_response_payload_bytes" or
            "moodle_webservice_transport_attempts" or "moodle_webservice_retries" or "moodle_webservice_logical_retries" or
            "moodle_read_gate_events" or "moodle_read_gate_wait_ms" or "moodle_read_gate_active" or "moodle_read_gate_queue_depth" or
            "moodle_forum_post_read_batches" or "moodle_forum_post_read_discussions" or
            "moodle_forum_post_read_calls" or "moodle_forum_post_read_failures" or
            "moodle_forum_post_read_max_concurrency" or "moodle_forum_post_read_duration_ms",
        "Moodle.McpTools" => instrument.Name is
            "mcp_tool_invocations" or "mcp_tool_failures" or "mcp_tool_duration_ms" or "mcp_http_rate_limit_rejections",
        "Moodle.Admission" => instrument.Name is
            "admission_candidate_decisions" or "admission_candidate_partition_overflows",
        "Moodle.Snapshots" => instrument.Name is
            "moodle_snapshot_l1_hits" or "moodle_snapshot_l2_hits" or "moodle_snapshot_misses" or
            "moodle_snapshot_refreshes" or "moodle_snapshot_age_seconds" or "moodle_snapshot_sync_duration_ms" or
            "moodle_snapshot_queue_enqueued" or "moodle_snapshot_queue_rejected" or "moodle_snapshot_throttle_waits" or
            "moodle_snapshot_runs_cleaned" or "moodle_snapshot_payload_bytes" or "moodle_gradebook_reads" or
            "moodle_gradebook_fallbacks" or "moodle_gradebook_cache_hits" or
            "moodle_snapshot_identity_resolution_duration_ms" or "moodle_snapshot_identity_resolution_commands" or
            "moodle_snapshot_database_read_duration_ms" or "moodle_snapshot_database_read_commands" or
            "moodle_snapshot_queue_wait_ms" or "moodle_snapshot_lease_claim_duration_ms" or
            "moodle_snapshot_lease_claim_commands" or "moodle_snapshot_transaction_duration_ms" or
            "moodle_snapshot_transaction_commands" or "moodle_snapshot_publish_duration_ms" or
            "moodle_snapshot_publish_commands" or "moodle_course_overview_duration_ms" or
            "moodle_course_overview_response_payload_bytes" or "moodle_course_overview_observations",
        _ => false
    };

    private bool IsIncludedMeter(Meter meter) =>
        (moodleMeterFilter is null && mcpMeterFilter is null && snapshotMeterFilter is null && admissionMeterFilter is null) ||
        ReferenceEquals(moodleMeterFilter, meter) ||
        ReferenceEquals(mcpMeterFilter, meter) ||
        ReferenceEquals(snapshotMeterFilter, meter) ||
        ReferenceEquals(admissionMeterFilter, meter);

    private void OnDoubleMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (!double.IsFinite(measurement) || measurement < 0)
        {
            return;
        }

        RecordValue(instrument, measurement, tags);
    }

    private void OnLongMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (measurement < 0 || (measurement == 0 && !IsValueHistogram(instrument.Name)))
        {
            return;
        }

        if (IsValueHistogram(instrument.Name))
        {
            RecordValue(instrument, measurement, tags);
        }
        else
        {
            var key = CreateKey(instrument, tags);
            lock (sync)
            {
                GetOrCreateAggregate(key).RecordCount(measurement);
            }
        }
    }

    private void RecordValue(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var key = CreateKey(instrument, tags);
        lock (sync)
        {
            GetOrCreateAggregate(key).RecordValue(measurement);
        }
    }

    private Aggregate GetOrCreateAggregate(SeriesKey key)
    {
        if (series.TryGetValue(key, out var aggregate))
        {
            return aggregate;
        }

        if (key != SeriesKey.Other && series.Count < MaxSeries - 1)
        {
            aggregate = new Aggregate();
            series.Add(key, aggregate);
            return aggregate;
        }

        if (!series.TryGetValue(SeriesKey.Other, out aggregate))
        {
            aggregate = new Aggregate();
            series.Add(SeriesKey.Other, aggregate);
        }

        return aggregate;
    }

    private static SeriesKey CreateKey(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var isMoodle = instrument.Meter.Name == "Moodle.MoodleCalls";
        var isSnapshot = instrument.Meter.Name == "Moodle.Snapshots";
        var isAdmission = instrument.Meter.Name == "Moodle.Admission";
        return new SeriesKey(
            isAdmission ? "admission" : isSnapshot ? "snapshot" : isMoodle ? "moodle" : "mcp",
            instrument.Name,
            SafeTag(ReadStringTag(tags, "tool"), "unknown", 80),
            SafeTag(ReadStringTag(tags, "canonical_operation"), "unknown", 80),
            SafeTag(ReadStringTag(tags, "function"), "unknown", 64),
            SafeTag(ReadStringTag(tags, "operation_kind"), "unknown", 16),
            SafeTag(ReadStringTag(tags, "outcome"), "unknown", 32),
            SafeErrorCode(ReadStringTag(tags, "error_code")),
            ReadHttpStatus(tags),
            SafeConnection(ReadStringTag(tags, "connection")),
            ReadPositiveIntTag(tags, "attempt"),
            ReadPositiveIntTag(tags, "retry_attempt"),
            isSnapshot ? SafeTag(ReadStringTag(tags, "dataset"), "unknown", 64) : "unknown",
            isSnapshot ? SafeTag(ReadStringTag(tags, "scope"), "unknown", 32) : "unknown",
            isSnapshot ? SafeTag(ReadStringTag(tags, "stage"), "unknown", 32) : "unknown",
            isAdmission ? SafeTag(ReadStringTag(tags, "traffic_class"), "unknown", 32) : "unknown",
            isAdmission ? SafeTag(ReadStringTag(tags, "family"), "unknown", 64) : "unknown",
            isAdmission ? SafeTag(ReadStringTag(tags, "decision"), "unknown", 32) : "unknown",
            isAdmission ? SafeTag(ReadStringTag(tags, "partition_kind"), "unknown", 16) : "unknown");
    }

    private static bool IsValueHistogram(string instrumentName) =>
        instrumentName.EndsWith("_duration_ms", StringComparison.Ordinal) ||
        instrumentName.EndsWith("_payload_bytes", StringComparison.Ordinal) ||
        instrumentName.EndsWith("_wait_ms", StringComparison.Ordinal) ||
        instrumentName.EndsWith("_active", StringComparison.Ordinal) ||
        instrumentName.EndsWith("_queue_depth", StringComparison.Ordinal);

    private static string SafeTag(string value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= maxLength && normalized.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? normalized
            : Other;
    }

    private static string SafeErrorCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var code = MoodleErrorContract.NormalizeCode(value);
        return SafeTag(code, Other, 64);
    }

    private static string SafeConnection(string value) =>
        value.Length == 18 && value.StartsWith("c_", StringComparison.Ordinal) &&
        value.AsSpan(2).ToString().All(Uri.IsHexDigit)
            ? value
            : string.IsNullOrWhiteSpace(value) ? "unknown" : Other;

    private static int? ReadHttpStatus(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var raw = ReadStringTag(tags, "http_status");
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var status) && status is >= 100 and <= 599
            ? status
            : null;
    }

    private static int? ReadPositiveIntTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        var raw = ReadStringTag(tags, key);
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 6
            ? value
            : null;
    }

    private static string ReadStringTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal) && tag.Value is not null)
            {
                return Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private RollupSnapshot FlushSnapshot()
    {
        Dictionary<SeriesKey, AggregateSnapshot> snapshot;
        lock (sync)
        {
            snapshot = series.ToDictionary(entry => entry.Key, entry => entry.Value.Snapshot());
            series.Clear();
        }

        var completedAt = DateTimeOffset.UtcNow;
        foreach (var (key, aggregate) in snapshot)
        {
            var isDuration = key.Metric.EndsWith("_duration_ms", StringComparison.Ordinal);
            var isWait = key.Metric.EndsWith("_wait_ms", StringComparison.Ordinal);
            var isPayload = key.Metric.EndsWith("_payload_bytes", StringComparison.Ordinal);
            var isValue = key.Metric.EndsWith("_active", StringComparison.Ordinal) ||
                          key.Metric.EndsWith("_queue_depth", StringComparison.Ordinal) ||
                          key.Metric.EndsWith("_age_seconds", StringComparison.Ordinal);
            logger.LogInformation(
                "moodle_mcp_metrics_window Source={Source} Metric={Metric} Tool={Tool} CanonicalOperation={CanonicalOperation} Function={Function} OperationKind={OperationKind} Outcome={Outcome} ErrorCode={ErrorCode} HttpStatus={HttpStatus} Attempt={Attempt} RetryAttempt={RetryAttempt} Connection={Connection} Dataset={Dataset} Scope={Scope} Stage={Stage} TrafficClass={TrafficClass} Family={Family} Decision={Decision} PartitionKind={PartitionKind} Count={Count} Samples={Samples} P50Ms={P50Ms} P95Ms={P95Ms} P99Ms={P99Ms} MaxMs={MaxMs} P50Bytes={P50Bytes} P95Bytes={P95Bytes} P99Bytes={P99Bytes} MaxBytes={MaxBytes} P50Value={P50Value} P95Value={P95Value} P99Value={P99Value} MaxValue={MaxValue} WindowEndUtc={WindowEndUtc}",
                key.Source,
                key.Metric,
                key.Tool,
                key.CanonicalOperation,
                key.Function,
                key.OperationKind,
                key.Outcome,
                key.ErrorCode,
                key.HttpStatus,
                key.Attempt,
                key.RetryAttempt,
                key.Connection,
                key.Dataset,
                key.Scope,
                key.Stage,
                key.TrafficClass,
                key.Family,
                key.Decision,
                key.PartitionKind,
                aggregate.Count,
                aggregate.SampleCount,
                isDuration || isWait ? aggregate.P50 : null,
                isDuration || isWait ? aggregate.P95 : null,
                isDuration || isWait ? aggregate.P99 : null,
                isDuration || isWait ? aggregate.Max : null,
                isPayload ? aggregate.P50 : null,
                isPayload ? aggregate.P95 : null,
                isPayload ? aggregate.P99 : null,
                isPayload ? aggregate.Max : null,
                isValue ? aggregate.P50 : null,
                isValue ? aggregate.P95 : null,
                isValue ? aggregate.P99 : null,
                isValue ? aggregate.Max : null,
                completedAt);
        }

        return new RollupSnapshot(snapshot);
    }

    private void RecordProcessResources()
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var timestamp = Stopwatch.GetTimestamp();
        double? cpuPercent = null;
        if (previousProcessCpu is { } previousCpu && previousProcessSampleTimestamp is { } previousTimestamp)
        {
            var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalMilliseconds;
            if (elapsed > 0)
            {
                cpuPercent = Math.Clamp((cpu - previousCpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100, 0, 100);
            }
        }

        previousProcessCpu = cpu;
        previousProcessSampleTimestamp = timestamp;
        if (cpuPercent is { } measuredCpu)
        {
            processCpuSamples.Add(measuredCpu);
        }

        workingSetSamples.Add(process.WorkingSet64);
    }

    private void FlushProcessResources()
    {
        var cpu = Percentiles(processCpuSamples);
        var workingSet = Percentiles(workingSetSamples);
        logger.LogInformation(
            "moodle_mcp_resource_window CpuSamples={CpuSamples} CpuP50Percent={CpuP50Percent} CpuP95Percent={CpuP95Percent} CpuP99Percent={CpuP99Percent} CpuMaxPercent={CpuMaxPercent} WorkingSetSamples={WorkingSetSamples} WorkingSetP50Bytes={WorkingSetP50Bytes} WorkingSetP95Bytes={WorkingSetP95Bytes} WorkingSetP99Bytes={WorkingSetP99Bytes} WorkingSetMaxBytes={WorkingSetMaxBytes} WindowEndUtc={WindowEndUtc}",
            processCpuSamples.Count,
            cpu.P50,
            cpu.P95,
            cpu.P99,
            cpu.Max,
            workingSetSamples.Count,
            workingSet.P50,
            workingSet.P95,
            workingSet.P99,
            workingSet.Max,
            DateTimeOffset.UtcNow);
        processCpuSamples.Clear();
        workingSetSamples.Clear();
    }

    private static (double? P50, double? P95, double? P99, double? Max) Percentiles(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return (null, null, null, null);
        }

        var sorted = samples.Order().ToArray();
        double? Quantile(double percentile)
        {
            var rank = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile), 1, sorted.Length);
            return sorted[rank - 1];
        }

        return (Quantile(0.50), Quantile(0.95), Quantile(0.99), sorted[^1]);
    }

    internal readonly record struct SeriesKey(
        string Source,
        string Metric,
        string Tool,
        string CanonicalOperation,
        string Function,
        string OperationKind,
        string Outcome,
        string ErrorCode,
        int? HttpStatus,
        string Connection,
        int? Attempt,
        int? RetryAttempt,
        string Dataset,
        string Scope,
        string Stage,
        string TrafficClass,
        string Family,
        string Decision,
        string PartitionKind)
    {
        internal static SeriesKey Other { get; } = new(
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            null,
            MoodleMetricsRollupService.Other,
            null,
            null,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other,
            MoodleMetricsRollupService.Other);

    }

    private sealed class Aggregate
    {
        private readonly long[] buckets = new long[Buckets.Length];
        private long samples;
        private double max;

        public long Count { get; private set; }

        public void RecordCount(long amount) => Count = Math.Min(long.MaxValue, Count + amount);

        public void RecordValue(double value)
        {
            Count++;
            samples++;
            max = Math.Max(max, value);
            var index = Array.FindIndex(Buckets, upperBound => value <= upperBound);
            buckets[index < 0 ? buckets.Length - 1 : index]++;
        }

        public AggregateSnapshot Snapshot() => new(
            Count,
            buckets.Sum(),
            Quantile(0.50),
            Quantile(0.95),
            Quantile(0.99),
            max);

        private double? Quantile(double percentile)
        {
            if (samples == 0)
            {
                return null;
            }

            var rank = Math.Max(1, (long)Math.Ceiling(samples * percentile));
            long cumulative = 0;
            for (var index = 0; index < buckets.Length; index++)
            {
                cumulative += buckets[index];
                if (cumulative >= rank)
                {
                    return double.IsPositiveInfinity(Buckets[index]) ? max : Buckets[index];
                }
            }

            return max;
        }
    }

    internal sealed record AggregateSnapshot(
        long Count,
        long SampleCount,
        double? P50,
        double? P95,
        double? P99,
        double Max);

    internal sealed class RollupSnapshot(IReadOnlyDictionary<SeriesKey, AggregateSnapshot> aggregates)
    {
        internal int SeriesCount => aggregates.Count;

        internal long GetCount(string source, string metric) => aggregates
            .Where(entry => entry.Key.Source == source && entry.Key.Metric == metric)
            .Sum(entry => entry.Value.Count);
    }
}
