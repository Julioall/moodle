using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;

namespace Moodle.Infrastructure.MoodleApi;

/// <summary>
/// Process-local, bounded scheduler for Moodle egress. It only controls
/// transport admission; callers retain their own operation and retry safety.
/// </summary>
internal sealed class MoodleAdmissionCoordinator : IMoodleAdmissionCoordinator, IDisposable
{
    private const string QueueFullCode = "moodle_admission_queue_full";
    private const string QueueTimeoutCode = "moodle_admission_queue_timeout";
    private const string DeadlineCode = "moodle_admission_deadline";
    private const string CapacityCode = "moodle_admission_state_capacity";
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    private readonly object _gate = new();
    private readonly MoodleAdmissionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly LinkedList<Ticket> _interactive = [];
    private readonly LinkedList<Ticket> _background = [];
    private readonly Dictionary<ConnectionKey, PartitionState> _connections = [];
    private readonly Dictionary<string, PartitionState> _installations = new(StringComparer.Ordinal);
    private readonly TokenBucket? _globalRate;
    private ITimer? _pumpTimer;
    private DateTimeOffset _overflowCooldownUntilUtc;
    private int _activeGlobal;
    private int _queuedGlobal;
    private int _interactiveGrantsInCycle;
    private int _backgroundGrantsInCycle;
    private bool _disposed;

    public MoodleAdmissionCoordinator(
        IOptions<MoodleAdmissionOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Value;
        _timeProvider = timeProvider;
        if (!_options.HasValidCoordinatorProfile)
            throw new InvalidOperationException("MoodleAdmission limits must be explicit and within supported bounds before the coordinator is constructed.");

        if (_options.PermitsPerMinuteGlobal is { } permits && _options.GlobalBurst is { } burst)
            _globalRate = new TokenBucket(permits, burst, _timeProvider);
    }

    internal int ActiveGlobalForTesting
    {
        get { lock (_gate) return _activeGlobal; }
    }

    internal int QueuedGlobalForTesting
    {
        get { lock (_gate) return _queuedGlobal; }
    }

    internal int ActiveConnectionForTesting(string ownerId, string connectionId)
    {
        lock (_gate)
            return _connections.TryGetValue(new ConnectionKey(ownerId, connectionId), out var state) ? state.Active : 0;
    }

    internal int ActiveInstallationForTesting(string installationId)
    {
        lock (_gate)
            return _installations.TryGetValue(installationId, out var state) ? state.Active : 0;
    }

    public async ValueTask<IMoodleAdmissionLease> AcquireAsync(
        MoodleAdmissionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateContext(context);
        cancellationToken.ThrowIfCancellationRequested();

        Ticket ticket;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            if (context.DeadlineUtc <= now)
                throw new MoodleAdmissionException(DeadlineCode, "The Moodle operation deadline has expired.");

            RemoveIdlePartitionsLocked(now);
            var connectionKey = new ConnectionKey(context.OwnerId, context.ConnectionId);
            if ((!_connections.ContainsKey(connectionKey) && _connections.Count >= _options.MaximumTrackedConnections) ||
                (!_installations.ContainsKey(context.InstallationId) && _installations.Count >= _options.MaximumTrackedInstallations))
                throw new MoodleAdmissionException(CapacityCode, "The Moodle admission state capacity is full.");
            var connection = GetOrCreatePartitionLocked(_connections, connectionKey, _options.MaximumTrackedConnections, PartitionKind.Connection);
            var installation = GetOrCreatePartitionLocked(_installations, context.InstallationId, _options.MaximumTrackedInstallations, PartitionKind.Installation);
            connection.LastAccessTimestamp = installation.LastAccessTimestamp = _timeProvider.GetTimestamp();

            ticket = new Ticket(context, connectionKey, context.InstallationId, connection, installation,
                Min(context.DeadlineUtc, now.AddSeconds(_options.QueueWaitTimeoutSeconds)));
            var queue = context.Priority == MoodleAdmissionPriority.Background ? _background : _interactive;
            ticket.Node = queue.AddLast(ticket);
            connection.Queued++;
            installation.Queued++;
            _queuedGlobal++;
            ticket.CancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var cancellation = (CancellationState)state!;
                    cancellation.Coordinator.CancelQueued(cancellation.Ticket, cancellation.Token);
                },
                new CancellationState(this, ticket, cancellationToken));

            PumpLocked();
            if (ticket.Status == TicketStatus.Queued && IsQueueOverLimit(ticket))
            {
                RemoveQueuedTicketLocked(ticket);
                ticket.Status = TicketStatus.Rejected;
                ticket.CancellationRegistration.Unregister();
                PumpLocked();
                throw new MoodleAdmissionException(QueueFullCode, "The Moodle egress queue is full.");
            }
        }

        return await ticket.Completion.Task.ConfigureAwait(false);
    }

    public void RecordRateLimit(
        MoodleAdmissionContext context,
        TimeSpan retryAfter,
        MoodleCooldownScope scope = MoodleCooldownScope.Connection)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (retryAfter <= TimeSpan.Zero) return;
        ValidateContext(context);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            var until = AddSaturating(now, retryAfter);
            if (scope == MoodleCooldownScope.Installation)
            {
                if (_installations.TryGetValue(context.InstallationId, out var installation))
                {
                    installation.CooldownUntilUtc = Max(installation.CooldownUntilUtc, until);
                    installation.LastAccessTimestamp = _timeProvider.GetTimestamp();
                }
                else if (_installations.Count < _options.MaximumTrackedInstallations)
                {
                    _installations.Add(context.InstallationId, new PartitionState(_options, _timeProvider, PartitionKind.Installation)
                    {
                        CooldownUntilUtc = until
                    });
                }
                else
                {
                    _overflowCooldownUntilUtc = Max(_overflowCooldownUntilUtc, until);
                }
            }
            else
            {
                var key = new ConnectionKey(context.OwnerId, context.ConnectionId);
                if (_connections.TryGetValue(key, out var connection))
                {
                    connection.CooldownUntilUtc = Max(connection.CooldownUntilUtc, until);
                    connection.LastAccessTimestamp = _timeProvider.GetTimestamp();
                }
                else if (_connections.Count < _options.MaximumTrackedConnections)
                {
                    _connections.Add(key, new PartitionState(_options, _timeProvider, PartitionKind.Connection)
                    {
                        CooldownUntilUtc = until
                    });
                }
                else
                {
                    _overflowCooldownUntilUtc = Max(_overflowCooldownUntilUtc, until);
                }
            }

            PumpLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pumpTimer?.Dispose();
            _pumpTimer = null;
            foreach (var ticket in _interactive.Concat(_background).ToArray())
            {
                RemoveQueuedTicketLocked(ticket);
                ticket.Status = TicketStatus.Rejected;
                ticket.CancellationRegistration.Unregister();
                ticket.Completion.TrySetException(new ObjectDisposedException(nameof(MoodleAdmissionCoordinator)));
            }
            _connections.Clear();
            _installations.Clear();
        }
    }

    private static void ValidateContext(MoodleAdmissionContext context)
    {
        if (!Enum.IsDefined(context.Operation))
            throw new ArgumentOutOfRangeException(nameof(context), "A supported Moodle admission operation is required.");
        if (string.IsNullOrWhiteSpace(context.OwnerId) || context.OwnerId.Length > 128 ||
            string.IsNullOrWhiteSpace(context.ConnectionId) || context.ConnectionId.Length > 128 ||
            string.IsNullOrWhiteSpace(context.InstallationId) || context.InstallationId.Length > 128 ||
            context.AttemptBudget is null)
            throw new ArgumentException("A bounded owner, connection, installation, and attempt budget are required.", nameof(context));
    }

    private PartitionState GetOrCreatePartitionLocked<TKey>(
        Dictionary<TKey, PartitionState> partitions,
        TKey key,
        int maximum,
        PartitionKind kind) where TKey : notnull
    {
        if (partitions.TryGetValue(key, out var existing)) return existing;
        if (partitions.Count >= maximum)
            throw new MoodleAdmissionException(CapacityCode, "The Moodle admission state capacity is full.");
        var created = new PartitionState(_options, _timeProvider, kind);
        partitions.Add(key, created);
        return created;
    }

    private bool IsQueueOverLimit(Ticket ticket) =>
        _queuedGlobal > _options.QueueLimitGlobal ||
        ticket.Connection.Queued > _options.QueueLimitPerConnection ||
        ticket.Installation.Queued > _options.QueueLimitPerInstallation;

    private void CancelQueued(Ticket ticket, CancellationToken cancellationToken)
    {
        var cancelled = false;
        lock (_gate)
        {
            if (_disposed || ticket.Status != TicketStatus.Queued) return;
            RemoveQueuedTicketLocked(ticket);
            ticket.Status = TicketStatus.Cancelled;
            ticket.Completion.TrySetCanceled(cancellationToken);
            PumpLocked();
            cancelled = true;
        }
        if (cancelled) ticket.CancellationRegistration.Unregister();
    }

    private void PumpLocked()
    {
        if (_disposed) return;
        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        RemoveIdlePartitionsLocked(now);

        foreach (var ticket in _interactive.Concat(_background).ToArray())
        {
            if (ticket.QueueDeadlineUtc > now) continue;
            RemoveQueuedTicketLocked(ticket);
            ticket.Status = TicketStatus.Rejected;
            ticket.CancellationRegistration.Unregister();
            var errorCode = ticket.Context.DeadlineUtc <= now ? DeadlineCode : QueueTimeoutCode;
            var message = errorCode == DeadlineCode
                ? "The Moodle operation deadline expired while waiting for admission."
                : "The Moodle admission queue wait limit was reached.";
            ticket.Completion.TrySetException(new MoodleAdmissionException(errorCode, message));
        }

        while (_activeGlobal < _options.MaxConcurrentGlobal)
        {
            DateTimeOffset? nextWakeUtc = null;
            var interactive = FindEligibleLocked(_interactive, now, timestamp, ref nextWakeUtc);
            var background = FindEligibleLocked(_background, now, timestamp, ref nextWakeUtc);
            if (interactive is null && background is null)
            {
                SchedulePumpLocked(nextWakeUtc);
                return;
            }

            Ticket selected;
            if (interactive is not null && background is not null)
            {
                if (_interactiveGrantsInCycle >= _options.InteractiveGrantWeight &&
                    _backgroundGrantsInCycle >= _options.BackgroundGrantWeight)
                {
                    _interactiveGrantsInCycle = 0;
                    _backgroundGrantsInCycle = 0;
                }

                if (_interactiveGrantsInCycle < _options.InteractiveGrantWeight)
                {
                    selected = interactive;
                    _interactiveGrantsInCycle++;
                }
                else
                {
                    selected = background;
                    _backgroundGrantsInCycle++;
                }
            }
            else
            {
                selected = interactive ?? background!;
            }

            GrantLocked(selected, timestamp);
        }

        var due = EarliestQueueDeadlineLocked();
        SchedulePumpLocked(due);
    }

    private Ticket? FindEligibleLocked(
        LinkedList<Ticket> queue,
        DateTimeOffset now,
        long timestamp,
        ref DateTimeOffset? nextWakeUtc)
    {
        foreach (var ticket in queue)
        {
            IncludeEarlier(ref nextWakeUtc, ticket.QueueDeadlineUtc);
            if (ticket.Connection.Active >= _options.MaxConcurrentPerConnection ||
                ticket.Installation.Active >= _options.MaxConcurrentPerInstallation)
                continue;

            var cooldown = Max(_overflowCooldownUntilUtc,
                Max(ticket.Connection.CooldownUntilUtc, ticket.Installation.CooldownUntilUtc));
            if (cooldown > now)
            {
                IncludeEarlier(ref nextWakeUtc, cooldown);
                continue;
            }

            var rateWait = GetRateWait(ticket, timestamp);
            if (rateWait > TimeSpan.Zero)
            {
                IncludeEarlier(ref nextWakeUtc, AddSaturating(now, rateWait));
                continue;
            }

            return ticket;
        }
        return null;
    }

    private TimeSpan GetRateWait(Ticket ticket, long timestamp)
    {
        var global = _globalRate?.GetWait(timestamp) ?? TimeSpan.Zero;
        var installation = ticket.Installation.Rate?.GetWait(timestamp) ?? TimeSpan.Zero;
        var connection = ticket.Connection.Rate?.GetWait(timestamp) ?? TimeSpan.Zero;
        return Max(global, Max(installation, connection));
    }

    private void GrantLocked(Ticket ticket, long timestamp)
    {
        var queue = ticket.Context.Priority == MoodleAdmissionPriority.Background ? _background : _interactive;
        queue.Remove(ticket.Node!);
        ticket.Node = null;
        ticket.Connection.Queued--;
        ticket.Installation.Queued--;
        _queuedGlobal--;

        _globalRate?.Consume(timestamp);
        ticket.Installation.Rate?.Consume(timestamp);
        ticket.Connection.Rate?.Consume(timestamp);
        ticket.Connection.Active++;
        ticket.Installation.Active++;
        ticket.Connection.LastAccessTimestamp = ticket.Installation.LastAccessTimestamp = timestamp;
        _activeGlobal++;
        ticket.Status = TicketStatus.Granted;
        ticket.CancellationRegistration.Unregister();
        ticket.Completion.TrySetResult(new Lease(this, ticket));
    }

    private void Release(Ticket ticket, bool refundRateReservation)
    {
        lock (_gate)
        {
            if (_disposed || ticket.Status != TicketStatus.Granted) return;
            ticket.Status = TicketStatus.Released;
            ticket.Connection.Active--;
            ticket.Installation.Active--;
            _activeGlobal--;
            var timestamp = _timeProvider.GetTimestamp();
            ticket.Connection.LastAccessTimestamp = ticket.Installation.LastAccessTimestamp = timestamp;
            if (refundRateReservation)
            {
                _globalRate?.Refund(timestamp);
                ticket.Installation.Rate?.Refund(timestamp);
                ticket.Connection.Rate?.Refund(timestamp);
            }
            PumpLocked();
        }
    }

    private void RemoveQueuedTicketLocked(Ticket ticket)
    {
        if (ticket.Status != TicketStatus.Queued || ticket.Node is null) return;
        var queue = ticket.Context.Priority == MoodleAdmissionPriority.Background ? _background : _interactive;
        queue.Remove(ticket.Node);
        ticket.Node = null;
        ticket.Connection.Queued--;
        ticket.Installation.Queued--;
        _queuedGlobal--;
        ticket.Connection.LastAccessTimestamp = ticket.Installation.LastAccessTimestamp = _timeProvider.GetTimestamp();
    }

    private void RemoveIdlePartitionsLocked(DateTimeOffset nowUtc)
    {
        var now = _timeProvider.GetTimestamp();
        var idle = TimeSpan.FromMinutes(_options.IdlePartitionExpirationMinutes);
        foreach (var (key, state) in _connections.ToArray())
        {
            if (state.Active == 0 && state.Queued == 0 && state.CooldownUntilUtc <= nowUtc &&
                _timeProvider.GetElapsedTime(state.LastAccessTimestamp, now) >= idle)
                _connections.Remove(key);
        }
        foreach (var (key, state) in _installations.ToArray())
        {
            if (state.Active == 0 && state.Queued == 0 && state.CooldownUntilUtc <= nowUtc &&
                _timeProvider.GetElapsedTime(state.LastAccessTimestamp, now) >= idle)
                _installations.Remove(key);
        }
        if (_overflowCooldownUntilUtc <= nowUtc) _overflowCooldownUntilUtc = DateTimeOffset.MinValue;
    }

    private DateTimeOffset? EarliestQueueDeadlineLocked()
    {
        DateTimeOffset? result = null;
        foreach (var ticket in _interactive.Concat(_background)) IncludeEarlier(ref result, ticket.QueueDeadlineUtc);
        return result;
    }

    private void SchedulePumpLocked(DateTimeOffset? dueUtc)
    {
        if (dueUtc is null)
        {
            _pumpTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        _pumpTimer ??= _timeProvider.CreateTimer(static state => ((MoodleAdmissionCoordinator)state!).OnPumpTimer(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var delay = dueUtc.Value - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(1);
        if (delay > MaxTimerDelay) delay = MaxTimerDelay;
        _pumpTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnPumpTimer()
    {
        lock (_gate) PumpLocked();
    }

    private static void IncludeEarlier(ref DateTimeOffset? current, DateTimeOffset candidate)
    {
        if (current is null || candidate < current.Value) current = candidate;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static DateTimeOffset AddSaturating(DateTimeOffset value, TimeSpan delay)
    {
        try { return value + delay; }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MaxValue; }
    }

    private sealed class Ticket(
        MoodleAdmissionContext context,
        ConnectionKey connectionKey,
        string installationKey,
        PartitionState connection,
        PartitionState installation,
        DateTimeOffset queueDeadlineUtc)
    {
        public MoodleAdmissionContext Context { get; } = context;
        public ConnectionKey ConnectionKey { get; } = connectionKey;
        public string InstallationKey { get; } = installationKey;
        public PartitionState Connection { get; } = connection;
        public PartitionState Installation { get; } = installation;
        public DateTimeOffset QueueDeadlineUtc { get; } = queueDeadlineUtc;
        public TaskCompletionSource<IMoodleAdmissionLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Ticket>? Node { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public TicketStatus Status { get; set; } = TicketStatus.Queued;
    }

    private sealed class Lease(MoodleAdmissionCoordinator owner, Ticket ticket) : IMoodleAdmissionLease
    {
        private int _attemptCommitState;
        private int _startedSuccessfully;
        private int _disposed;

        public bool TryStartAttempt()
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (Interlocked.CompareExchange(ref _attemptCommitState, 1, 0) != 0) return false;
            var started = ticket.Context.AttemptBudget.TryConsumeAttempt();
            Volatile.Write(ref _startedSuccessfully, started ? 1 : 0);
            return started;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Release(ticket, Volatile.Read(ref _startedSuccessfully) == 0);
        }
    }

    private sealed class PartitionState
    {
        public PartitionState(MoodleAdmissionOptions options, TimeProvider timeProvider, PartitionKind kind)
        {
            LastAccessTimestamp = timeProvider.GetTimestamp();
            var rate = kind == PartitionKind.Connection
                ? options.PermitsPerMinutePerConnection
                : options.PermitsPerMinutePerInstallation;
            var burst = kind == PartitionKind.Connection ? options.ConnectionBurst : options.InstallationBurst;
            if (rate is { } permits && burst is { } capacity)
                Rate = new TokenBucket(permits, capacity, timeProvider);
        }

        public int Active { get; set; }
        public int Queued { get; set; }
        public long LastAccessTimestamp { get; set; }
        public DateTimeOffset CooldownUntilUtc { get; set; }
        public TokenBucket? Rate { get; }
    }

    private sealed class TokenBucket(int permitsPerMinute, int burst, TimeProvider timeProvider)
    {
        private double _tokens = burst;
        private long _lastRefillTimestamp = timeProvider.GetTimestamp();

        public TimeSpan GetWait(long now)
        {
            Refill(now);
            return _tokens >= 1d
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((1d - _tokens) * 60d / permitsPerMinute);
        }

        public void Consume(long now)
        {
            Refill(now);
            _tokens = Math.Max(0d, _tokens - 1d);
        }

        public void Refund(long now)
        {
            Refill(now);
            _tokens = Math.Min(burst, _tokens + 1d);
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
    private sealed record CancellationState(
        MoodleAdmissionCoordinator Coordinator,
        Ticket Ticket,
        CancellationToken Token);
    private enum TicketStatus { Queued, Granted, Cancelled, Rejected, Released }
}
