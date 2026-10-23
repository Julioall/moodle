using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moodle.Presentation.Configuration;

namespace Moodle.Presentation.Security;

internal readonly record struct AdmissionCandidateDecision(
    bool IsAllowed,
    TimeSpan RetryAfter,
    bool UsesOverflowPartition,
    string PartitionKind);

/// <summary>
/// Simulates the candidate local-read token bucket while ingress is in
/// Observe mode. It never delays or rejects a request. Tracked state is bounded
/// and idle entries expire; overflow identities share a conservative bucket.
/// </summary>
internal sealed class IngressAdmissionObserver : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<PartitionKey, TokenBucketState> _partitions = [];
    private readonly Dictionary<string, TokenBucketState> _overflowPartitions = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _ratePerMinute;
    private readonly int _burst;
    private readonly int _maximumPartitions;
    private readonly TimeSpan _idleExpiration;
    private readonly Meter _meter = new("Moodle.Admission", "1.0.0");
    private readonly Counter<long> _decisions;
    private readonly Counter<long> _partitionOverflows;
    private long _evaluations;

    public IngressAdmissionObserver(IOptions<AdmissionRolloutOptions> options, TimeProvider timeProvider)
    {
        var value = options.Value;
        _ratePerMinute = value.LocalReadPermitLimitPerMinute;
        _burst = value.LocalReadBurst;
        _maximumPartitions = value.MaximumTrackedPartitions;
        _idleExpiration = TimeSpan.FromMinutes(value.IdlePartitionExpirationMinutes);
        _timeProvider = timeProvider;
        _decisions = _meter.CreateCounter<long>("admission_candidate_decisions");
        _partitionOverflows = _meter.CreateCounter<long>("admission_candidate_partition_overflows");
    }

    internal int TrackedPartitionCount
    {
        get
        {
            lock (_gate)
            {
                return _partitions.Count;
            }
        }
    }

    internal Meter InstrumentationMeter => _meter;

    internal AdmissionCandidateDecision Evaluate(HttpContext context, AdmissionRouteMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);

        var (key, partitionKind) = CreatePartitionKey(context, metadata.Family);
        var now = _timeProvider.GetTimestamp();
        TokenBucketState bucket;
        var usesOverflow = false;

        lock (_gate)
        {
            if ((++_evaluations & 0xFF) == 0)
            {
                RemoveIdlePartitions(now);
            }

            if (_partitions.TryGetValue(key, out bucket!))
            {
                bucket.ActiveEvaluations++;
            }
            else if (_partitions.Count < _maximumPartitions)
            {
                bucket = new TokenBucketState(_timeProvider, _ratePerMinute, _burst)
                {
                    ActiveEvaluations = 1
                };
                _partitions.Add(key, bucket);
            }
            else
            {
                if (!_overflowPartitions.TryGetValue(metadata.Family, out bucket!))
                {
                    bucket = new TokenBucketState(_timeProvider, _ratePerMinute, _burst);
                    _overflowPartitions.Add(metadata.Family, bucket);
                }

                bucket.ActiveEvaluations++;
                usesOverflow = true;
            }
        }

        bool allowed;
        TimeSpan retryAfter;
        try
        {
            allowed = bucket.TryAcquire(now, out retryAfter);
        }
        finally
        {
            lock (_gate)
            {
                bucket.ActiveEvaluations--;
                bucket.LastAccessTimestamp = now;
            }
        }

        var tags = new TagList
        {
            { "traffic_class", "local_read" },
            { "family", metadata.Family },
            { "decision", allowed ? "admitted" : "would_reject" },
            { "partition_kind", usesOverflow ? "overflow" : partitionKind }
        };
        _decisions.Add(1, tags);
        if (usesOverflow)
        {
            _partitionOverflows.Add(1, new TagList { { "family", metadata.Family } });
        }

        return new AdmissionCandidateDecision(allowed, retryAfter, usesOverflow, partitionKind);
    }

    public void Dispose() => _meter.Dispose();

    private void RemoveIdlePartitions(long now)
    {
        foreach (var pair in _partitions.ToArray())
        {
            if (pair.Value.ActiveEvaluations == 0 &&
                _timeProvider.GetElapsedTime(pair.Value.LastAccessTimestamp, now) >= _idleExpiration)
            {
                _partitions.Remove(pair.Key);
            }
        }
    }

    private static (PartitionKey Key, string PartitionKind) CreatePartitionKey(HttpContext context, string family)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue("sub");
            if (Guid.TryParse(subject, out var ownerId))
            {
                return (new PartitionKey("owner", ownerId.ToString("N"), family), "owner");
            }
        }

        var address = context.Connection.RemoteIpAddress;
        if (address is not null)
        {
            var normalizedAddress = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return (new PartitionKey("ip", normalizedAddress.ToString(), family), "ip");
        }

        return (new PartitionKey("fallback", "unknown", family), "unknown");
    }

    private readonly record struct PartitionKey(string Kind, string Identity, string Family);

    private sealed class TokenBucketState(TimeProvider timeProvider, int ratePerMinute, int burst)
    {
        private readonly object _gate = new();
        private double _tokens = burst;
        private long _lastRefillTimestamp = timeProvider.GetTimestamp();

        public int ActiveEvaluations { get; set; }

        public long LastAccessTimestamp { get; set; } = timeProvider.GetTimestamp();

        public bool TryAcquire(long now, out TimeSpan retryAfter)
        {
            lock (_gate)
            {
                var elapsed = timeProvider.GetElapsedTime(_lastRefillTimestamp, now);
                if (elapsed > TimeSpan.Zero)
                {
                    _tokens = Math.Min(burst, _tokens + elapsed.TotalSeconds * ratePerMinute / 60d);
                    _lastRefillTimestamp = now;
                }

                if (_tokens >= 1d)
                {
                    _tokens -= 1d;
                    retryAfter = TimeSpan.Zero;
                    return true;
                }

                retryAfter = TimeSpan.FromSeconds((1d - _tokens) * 60d / ratePerMinute);
                return false;
            }
        }
    }
}

internal sealed class IngressAdmissionObservationMiddleware(
    RequestDelegate next,
    IOptions<AdmissionRolloutOptions> rolloutOptions,
    IngressAdmissionObserver observer)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (rolloutOptions.Value.IngressMode == AdmissionMode.Observe &&
            context.GetEndpoint() is { } endpoint &&
            AdmissionEndpointClassifier.Classify(endpoint) is { } metadata &&
            metadata.TrafficClass == AdmissionTrafficClass.LocalRead)
        {
            _ = observer.Evaluate(context, metadata);
        }

        return next(context);
    }
}
