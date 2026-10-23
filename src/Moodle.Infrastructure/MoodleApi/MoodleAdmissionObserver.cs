using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;

namespace Moodle.Infrastructure.MoodleApi;

/// <summary>
/// Simulates egress admission decisions while Observe mode is active. It
/// records bounded candidate state but never queues, delays, or rejects an
/// HTTP request.
/// </summary>
internal sealed class MoodleAdmissionObserver : IDisposable
{
    private readonly object _gate = new();
    private readonly MoodleAdmissionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ConnectionKey, PartitionState> _connections = [];
    private readonly Dictionary<string, PartitionState> _installations = new(StringComparer.Ordinal);
    private readonly Meter _meter = new("Moodle.Admission", "1.0.0");
    private readonly Counter<long> _decisions;
    private readonly Counter<long> _partitionOverflows;
    private readonly TokenBucket? _globalRate;
    private DateTimeOffset _overflowCooldownUntilUtc;
    private int _activeGlobal;
    private long _evaluations;
    private bool _disposed;

    public MoodleAdmissionObserver(IOptions<MoodleAdmissionOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Value;
        _timeProvider = timeProvider;
        if (!_options.HasValidCoordinatorProfile)
            throw new InvalidOperationException("A valid MoodleAdmission candidate profile is required in Observe mode.");

        if (_options.PermitsPerMinuteGlobal is { } permits && _options.GlobalBurst is { } burst)
            _globalRate = new TokenBucket(permits, burst, _timeProvider);
        _decisions = _meter.CreateCounter<long>("admission_candidate_decisions");
        _partitionOverflows = _meter.CreateCounter<long>("admission_candidate_partition_overflows");
    }

    internal int ActiveGlobalForTesting
    {
        get { lock (_gate) return _activeGlobal; }
    }

    internal int TrackedConnectionCountForTesting
    {
        get { lock (_gate) return _connections.Count; }
    }

    internal IDisposable Begin(MoodleAdmissionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var attempt = context.AttemptBudget.RecordObservedAttempt();
        var overAttemptBudget = attempt > context.AttemptBudget.MaximumAttempts;
        var nowUtc = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((++_evaluations & 0xFF) == 0) RemoveIdlePartitions(nowUtc, timestamp);

            var connectionKey = new ConnectionKey(context.OwnerId, context.ConnectionId);
            var connectionExists = _connections.TryGetValue(connectionKey, out var connection);
            var installationExists = _installations.TryGetValue(context.InstallationId, out var installation);
            if ((!connectionExists && _connections.Count >= _options.MaximumTrackedConnections) ||
                (!installationExists && _installations.Count >= _options.MaximumTrackedInstallations))
            {
                RecordDecision(context, overAttemptBudget ? "would_reject" : "state_overflow", "overflow");
                _partitionOverflows.Add(1, new TagList { { "traffic_class", ToTrafficClass(context.Origin) } });
                return EmptyLease.Instance;
            }

            connection ??= GetOrAdd(_connections, connectionKey, PartitionKind.Connection);
            installation ??= GetOrAdd(_installations, context.InstallationId, PartitionKind.Installation);
            connection.LastAccessTimestamp = installation.LastAccessTimestamp = timestamp;

            var cooldown = Max(_overflowCooldownUntilUtc,
                Max(connection.CooldownUntilUtc, installation.CooldownUntilUtc));
            var overConcurrency = _activeGlobal >= _options.MaxConcurrentGlobal ||
                                  connection.Active >= _options.MaxConcurrentPerConnection ||
                                  installation.Active >= _options.MaxConcurrentPerInstallation;
            var rateAvailable = cooldown <= nowUtc &&
                                _globalRate?.CanConsume(timestamp) != false &&
                                installation.Rate?.CanConsume(timestamp) != false &&
                                connection.Rate?.CanConsume(timestamp) != false;

            var decision = overAttemptBudget
                ? "would_reject"
                : cooldown > nowUtc || !rateAvailable
                    ? "would_wait"
                    : overConcurrency
                        ? "would_queue"
                        : "admitted";

            if (decision == "admitted")
            {
                _globalRate?.Consume(timestamp);
                installation.Rate?.Consume(timestamp);
                connection.Rate?.Consume(timestamp);
            }

            RecordDecision(context, decision, "connection");
            _activeGlobal++;
            connection.Active++;
            installation.Active++;
            return new ObservationLease(this, connection, installation);
        }
    }

    internal void RecordRateLimit(
        MoodleAdmissionContext context,
        TimeSpan retryAfter,
        MoodleCooldownScope scope = MoodleCooldownScope.Connection)
    {
        if (retryAfter <= TimeSpan.Zero) return;
        lock (_gate)
        {
            if (_disposed) return;
            var until = AddSaturating(_timeProvider.GetUtcNow(), retryAfter);
            if (scope == MoodleCooldownScope.Installation)
            {
                if (!_installations.TryGetValue(context.InstallationId, out var state))
                {
                    if (_installations.Count >= _options.MaximumTrackedInstallations)
                    {
                        _overflowCooldownUntilUtc = Max(_overflowCooldownUntilUtc, until);
                        return;
                    }
                    state = GetOrAdd(_installations, context.InstallationId, PartitionKind.Installation);
                }
                state.CooldownUntilUtc = Max(state.CooldownUntilUtc, until);
                state.LastAccessTimestamp = _timeProvider.GetTimestamp();
            }
            else
            {
                var key = new ConnectionKey(context.OwnerId, context.ConnectionId);
                if (!_connections.TryGetValue(key, out var state))
                {
                    if (_connections.Count >= _options.MaximumTrackedConnections)
                    {
                        _overflowCooldownUntilUtc = Max(_overflowCooldownUntilUtc, until);
                        return;
                    }
                    state = GetOrAdd(_connections, key, PartitionKind.Connection);
                }
                state.CooldownUntilUtc = Max(state.CooldownUntilUtc, until);
                state.LastAccessTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connections.Clear();
            _installations.Clear();
            _meter.Dispose();
        }
    }

    private PartitionState GetOrAdd<TKey>(
        Dictionary<TKey, PartitionState> partitions,
        TKey key,
        PartitionKind kind) where TKey : notnull
    {
        if (partitions.TryGetValue(key, out var state)) return state;
        state = new PartitionState(_options, _timeProvider, kind);
        partitions.Add(key, state);
        return state;
    }

    private void Release(PartitionState connection, PartitionState installation)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _activeGlobal--;
            connection.Active--;
            installation.Active--;
            var timestamp = _timeProvider.GetTimestamp();
            connection.LastAccessTimestamp = installation.LastAccessTimestamp = timestamp;
        }
    }

    private void RemoveIdlePartitions(DateTimeOffset nowUtc, long timestamp)
    {
        var idle = TimeSpan.FromMinutes(_options.IdlePartitionExpirationMinutes);
        foreach (var (key, state) in _connections.ToArray())
        {
            if (state.Active == 0 && state.CooldownUntilUtc <= nowUtc &&
                _timeProvider.GetElapsedTime(state.LastAccessTimestamp, timestamp) >= idle)
                _connections.Remove(key);
        }
        foreach (var (key, state) in _installations.ToArray())
        {
            if (state.Active == 0 && state.CooldownUntilUtc <= nowUtc &&
                _timeProvider.GetElapsedTime(state.LastAccessTimestamp, timestamp) >= idle)
                _installations.Remove(key);
        }
        if (_overflowCooldownUntilUtc <= nowUtc) _overflowCooldownUntilUtc = DateTimeOffset.MinValue;
    }

    private void RecordDecision(MoodleAdmissionContext context, string decision, string partitionKind)
    {
        _decisions.Add(1, new TagList
        {
            { "traffic_class", ToTrafficClass(context.Origin) },
            { "family", ToFamily(context.Operation) },
            { "decision", decision },
            { "partition_kind", partitionKind }
        });
    }

    private static string ToTrafficClass(MoodleAdmissionOrigin origin) => origin switch
    {
        MoodleAdmissionOrigin.Mcp => "mcp",
        MoodleAdmissionOrigin.Portal => "portal",
        MoodleAdmissionOrigin.Snapshot => "snapshot",
        MoodleAdmissionOrigin.Report => "report",
        MoodleAdmissionOrigin.Grading => "grading",
        _ => "unknown"
    };

    private static string ToFamily(MoodleAdmissionOperation operation) => operation switch
    {
        MoodleAdmissionOperation.Read => "read",
        MoodleAdmissionOperation.Token => "token",
        MoodleAdmissionOperation.Write => "write",
        MoodleAdmissionOperation.Download => "download",
        _ => "unknown"
    };

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static DateTimeOffset AddSaturating(DateTimeOffset value, TimeSpan delay)
    {
        try { return value + delay; }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MaxValue; }
    }

    private sealed class ObservationLease(MoodleAdmissionObserver owner, PartitionState connection, PartitionState installation) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(connection, installation);
        }
    }

    private sealed class PartitionState
    {
        public PartitionState(MoodleAdmissionOptions options, TimeProvider timeProvider, PartitionKind kind)
        {
            LastAccessTimestamp = timeProvider.GetTimestamp();
            var permits = kind == PartitionKind.Connection
                ? options.PermitsPerMinutePerConnection
                : options.PermitsPerMinutePerInstallation;
            var burst = kind == PartitionKind.Connection ? options.ConnectionBurst : options.InstallationBurst;
            if (permits is { } rate && burst is { } capacity)
                Rate = new TokenBucket(rate, capacity, timeProvider);
        }

        public int Active { get; set; }
        public long LastAccessTimestamp { get; set; }
        public DateTimeOffset CooldownUntilUtc { get; set; }
        public TokenBucket? Rate { get; }
    }

    private sealed class TokenBucket(int permitsPerMinute, int burst, TimeProvider timeProvider)
    {
        private double _tokens = burst;
        private long _lastRefillTimestamp = timeProvider.GetTimestamp();

        public bool CanConsume(long now)
        {
            Refill(now);
            return _tokens >= 1d;
        }

        public void Consume(long now)
        {
            Refill(now);
            _tokens = Math.Max(0d, _tokens - 1d);
        }

        private void Refill(long now)
        {
            var elapsed = timeProvider.GetElapsedTime(_lastRefillTimestamp, now);
            if (elapsed <= TimeSpan.Zero) return;
            _tokens = Math.Min(burst, _tokens + elapsed.TotalSeconds * permitsPerMinute / 60d);
            _lastRefillTimestamp = now;
        }
    }

    private readonly record struct ConnectionKey(string OwnerId, string ConnectionId);
    private enum PartitionKind { Connection, Installation }

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public void Dispose() { }
    }
}
