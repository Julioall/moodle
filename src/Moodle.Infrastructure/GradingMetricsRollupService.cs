using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure;

/// <summary>
/// Emits bounded, minute-sized summaries from the existing Moodle and grading meters.
/// The listener retains only aggregate bucket counts and opaque connection tokens.
/// </summary>
internal sealed class GradingMetricsRollupService : BackgroundService, IDisposable
{
    private const int MaxSeries = 64;
    private const string Other = "other";
    private static readonly double[] DurationBuckets =
    [
        10, 50, 100, 250, 500, 1000, 2000, 5000, 10000, 30000, 60000, double.PositiveInfinity
    ];

    private readonly object sync = new();
    private readonly Dictionary<SeriesKey, Aggregate> series = [];
    private readonly ILogger<GradingMetricsRollupService> logger;
    private readonly TimeProvider timeProvider;
    private readonly MeterListener listener;
    private readonly Meter? moodleMeterFilter;
    private readonly Meter? gradingMeterFilter;
    private bool disposed;

    public GradingMetricsRollupService(
        ILogger<GradingMetricsRollupService> logger,
        TimeProvider timeProvider,
        Meter? moodleMeterFilter = null,
        Meter? gradingMeterFilter = null)
    {
        this.logger = logger;
        this.timeProvider = timeProvider;
        this.moodleMeterFilter = moodleMeterFilter;
        this.gradingMeterFilter = gradingMeterFilter;
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

    internal GradingMetricsRollupSnapshot FlushForTesting() => FlushSnapshot();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            FlushSnapshot();
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

    private static bool IsSupported(Instrument instrument)
    {
        var meterName = instrument.Meter.Name;
        if (meterName == "Moodle.MoodleCalls")
        {
            return instrument.Name == "moodle_webservice_duration_ms";
        }

        if (meterName != "Moodle.Grading")
        {
            return false;
        }

        return instrument.Name is "grading_phase_duration_ms"
            or "grading_package_issue_count"
            or "grading_package_outcomes"
            or "grading_package_retries";
    }

    private bool IsIncludedMeter(Meter meter) =>
        (moodleMeterFilter is null && gradingMeterFilter is null) ||
        ReferenceEquals(moodleMeterFilter, meter) ||
        ReferenceEquals(gradingMeterFilter, meter);

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

        var key = CreateKey(instrument, tags);
        lock (sync)
        {
            GetOrCreateAggregate(key).RecordDuration(measurement);
        }
    }

    private void OnLongMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (measurement <= 0)
        {
            return;
        }

        var key = CreateKey(instrument, tags);
        lock (sync)
        {
            GetOrCreateAggregate(key).RecordCount(measurement);
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

    private static SeriesKey CreateKey(
        Instrument instrument,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var source = instrument.Meter.Name == "Moodle.MoodleCalls" ? "moodle" : "grading";
        var function = ReadStringTag(tags, "function");
        var phase = ReadStringTag(tags, "phase");
        var errorCode = instrument.Name == "moodle_webservice_duration_ms"
            ? GetMoodleErrorCode(tags)
            : string.Empty;
        var result = instrument.Name switch
        {
            "moodle_webservice_duration_ms" => GetMoodleResult(tags),
            "grading_phase_duration_ms" => NormalizeLabel(ReadStringTag(tags, "result"), 48),
            "grading_package_issue_count" => NormalizeIssue(ReadStringTag(tags, "code")),
            "grading_package_outcomes" => NormalizePackageOutcome(ReadStringTag(tags, "status")),
            "grading_package_retries" => NormalizeIssue(ReadStringTag(tags, "reason")),
            _ => Other
        };

        if (instrument.Name == "grading_package_issue_count")
        {
            phase = "package_issue";
        }
        else if (instrument.Name == "grading_package_outcomes")
        {
            phase = "package_outcome";
        }
        else if (instrument.Name == "grading_package_retries")
        {
            phase = "package_retry";
            result = "retry_" + result;
        }
        else if (instrument.Name == "moodle_webservice_duration_ms")
        {
            phase = "webservice";
        }

        var status = ReadIntTag(tags, "http_status");
        var connection = HashConnection(ReadStringTag(tags, "connection"));
        return new SeriesKey(source, function, phase, result, errorCode, status, connection);
    }

    private static string GetMoodleResult(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var status = ReadIntTag(tags, "http_status");
        if (status == 429)
        {
            return "429";
        }

        var errorCode = GetMoodleErrorCode(tags);
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            return errorCode;
        }

        return "success";
    }

    private static string GetMoodleErrorCode(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var raw = ReadStringTag(tags, "error_code");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var code = MoodleErrorContract.NormalizeCode(raw);
        return code switch
        {
            MoodleErrorContract.RequestTimeout => "request_timeout",
            MoodleErrorContract.NetworkError => "network_error",
            MoodleErrorContract.AuthenticationFailed => "authentication_failed",
            MoodleErrorContract.PermissionDenied => "permission_denied",
            MoodleErrorContract.ConnectionNotFound => "connection_not_found",
            MoodleErrorContract.InvalidResponse => "invalid_response",
            MoodleErrorContract.ApiError => "api_error",
            _ => NormalizeLabel(code, 64)
        };
    }

    private static string NormalizeIssue(string value) => value.Trim().ToLowerInvariant() switch
    {
        "artifact_read_failed" => "artifact_read_failed",
        "context_unavailable" => "context_unavailable",
        "http_429" => "http_429",
        "timeout" => "timeout",
        "transient_dependency" => "transient_dependency",
        "finalization_conflict" => "finalization_conflict",
        "lease_lost" => "lease_lost",
        "package_failure" => "package_failure",
        _ => Other
    };

    private static string NormalizePackageOutcome(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ready" => "ready",
        "partially_ready" or "partiallyready" => "partially_ready",
        "results_received" or "resultsreceived" => "results_received",
        "preparing" => "preparing",
        "needs_attention" or "needsattention" => "needs_attention",
        "cancelled" => "cancelled",
        _ => Other
    };

    private static string NormalizeLabel(string value, int maxLength)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > maxLength ||
            normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            return Other;
        }

        return normalized;
    }

    private static string HashConnection(string connection)
    {
        if (string.IsNullOrWhiteSpace(connection))
        {
            return "unknown";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(connection));
        return "c_" + Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
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

    private static int? ReadIntTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal) &&
                int.TryParse(Convert.ToString(tag.Value, CultureInfo.InvariantCulture), out var status) &&
                status is >= 100 and <= 599)
            {
                return status;
            }
        }

        return null;
    }

    private GradingMetricsRollupSnapshot FlushSnapshot()
    {
        Dictionary<SeriesKey, AggregateSnapshot> snapshot;
        lock (sync)
        {
            snapshot = series.ToDictionary(entry => entry.Key, entry => entry.Value.Snapshot());
            series.Clear();
        }

        var completedAt = timeProvider.GetUtcNow();
        foreach (var (key, aggregate) in snapshot)
        {
            logger.LogInformation(
                "grading_metrics_minute Source={Source} Function={Function} Phase={Phase} Result={Result} ErrorCode={ErrorCode} HttpStatus={HttpStatus} Connection={Connection} Count={Count} P50Ms={P50Ms} P95Ms={P95Ms} WindowEndUtc={WindowEndUtc}",
                key.Source,
                key.Function,
                key.Phase,
                key.Result,
                key.ErrorCode,
                key.HttpStatus,
                key.Connection,
                aggregate.Count,
                aggregate.P50Ms,
                aggregate.P95Ms,
                completedAt);
        }

        return new GradingMetricsRollupSnapshot(snapshot);
    }

    internal readonly record struct SeriesKey(
        string Source,
        string Function,
        string Phase,
        string Result,
        string ErrorCode,
        int? HttpStatus,
        string Connection)
    {
        public static SeriesKey Other { get; } = new(
            GradingMetricsRollupService.Other,
            GradingMetricsRollupService.Other,
            GradingMetricsRollupService.Other,
            GradingMetricsRollupService.Other,
            GradingMetricsRollupService.Other,
            null,
            GradingMetricsRollupService.Other);
    }

    private sealed class Aggregate
    {
        private readonly long[] durationBuckets = new long[DurationBuckets.Length];
        private long count;
        private double maxDurationMs;

        public void RecordDuration(double durationMs)
        {
            count++;
            maxDurationMs = Math.Max(maxDurationMs, durationMs);
            var index = Array.FindIndex(DurationBuckets, upperBound => durationMs <= upperBound);
            durationBuckets[index < 0 ? durationBuckets.Length - 1 : index]++;
        }

        public void RecordCount(long amount) => count = Math.Min(long.MaxValue, count + amount);

        public AggregateSnapshot Snapshot() => new(count, (long[])durationBuckets.Clone(), maxDurationMs);
    }

    internal sealed record AggregateSnapshot(long Count, long[] DurationBuckets, double MaxDurationMs)
    {
        public double? P50Ms => Quantile(0.50);
        public double? P95Ms => Quantile(0.95);

        private double? Quantile(double percentile)
        {
            var durationCount = DurationBuckets.Sum();
            if (durationCount == 0)
            {
                return null;
            }

            var rank = Math.Max(1, (long)Math.Ceiling(durationCount * percentile));
            long cumulative = 0;
            for (var index = 0; index < DurationBuckets.Length; index++)
            {
                cumulative += DurationBuckets[index];
                if (cumulative >= rank)
                {
                    return double.IsPositiveInfinity(DurationBucketsBound(index))
                        ? MaxDurationMs
                        : DurationBucketsBound(index);
                }
            }

            return MaxDurationMs;
        }

        private static double DurationBucketsBound(int index) => GradingMetricsRollupService.DurationBuckets[index];
    }

    internal sealed class GradingMetricsRollupSnapshot(
        IReadOnlyDictionary<SeriesKey, AggregateSnapshot> snapshot)
    {
        internal int SeriesCount => snapshot.Count;

        internal long GetCount(string source, string result) => snapshot
            .Where(entry => string.Equals(entry.Key.Source, source, StringComparison.Ordinal) &&
                            string.Equals(entry.Key.Result, result, StringComparison.Ordinal))
            .Sum(entry => entry.Value.Count);
    }
}
