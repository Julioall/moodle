using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Infrastructure.MoodleApi;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleAdmissionCoordinatorTests
{
    [Fact]
    public async Task Enforces_global_installation_and_connection_concurrency_without_blocking_another_installation()
    {
        using var coordinator = CreateCoordinator(maxGlobal: 2, maxInstallation: 1, maxConnection: 1);
        var first = await coordinator.AcquireAsync(Context(connection: "connection-a", installation: "moodle-a"), default);
        var sameInstallation = coordinator.AcquireAsync(Context(connection: "connection-b", installation: "moodle-a"), default).AsTask();
        await WaitUntilAsync(() => coordinator.QueuedGlobalForTesting == 1);

        using var otherInstallation = await coordinator.AcquireAsync(Context(connection: "connection-c", installation: "moodle-b"), default);
        Assert.Equal(2, coordinator.ActiveGlobalForTesting);
        Assert.Equal(1, coordinator.ActiveInstallationForTesting("moodle-a"));
        Assert.Equal(1, coordinator.ActiveInstallationForTesting("moodle-b"));

        first.Dispose();
        using var nextSameInstallation = await sameInstallation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, coordinator.ActiveGlobalForTesting);
        Assert.Equal(1, coordinator.ActiveConnectionForTesting("owner-1", "connection-b"));
    }

    [Fact]
    public async Task Skips_a_connection_blocked_queue_entry_when_a_later_connection_is_eligible()
    {
        using var coordinator = CreateCoordinator(maxGlobal: 2, maxInstallation: 2, maxConnection: 1);
        var first = await coordinator.AcquireAsync(Context(connection: "connection-a"), default);
        var sameConnection = coordinator.AcquireAsync(Context(connection: "connection-a"), default).AsTask();
        await WaitUntilAsync(() => coordinator.QueuedGlobalForTesting == 1);

        using var anotherConnection = await coordinator.AcquireAsync(Context(connection: "connection-b"), default)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(sameConnection.IsCompleted);
        Assert.Equal(2, coordinator.ActiveGlobalForTesting);

        first.Dispose();
        using var nextSameConnection = await sameConnection.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Schedules_background_after_three_interactive_grants_when_both_are_eligible()
    {
        using var coordinator = CreateCoordinator(maxGlobal: 1, maxInstallation: 1, maxConnection: 1);
        var first = await coordinator.AcquireAsync(Context(), default);
        var waiters = new (string Name, Task<IMoodleAdmissionLease> Task)[]
        {
            ("interactive-1", coordinator.AcquireAsync(Context(), default).AsTask()),
            ("interactive-2", coordinator.AcquireAsync(Context(), default).AsTask()),
            ("interactive-3", coordinator.AcquireAsync(Context(), default).AsTask()),
            ("interactive-4", coordinator.AcquireAsync(Context(), default).AsTask()),
            ("background", coordinator.AcquireAsync(Context(priority: MoodleAdmissionPriority.Background), default).AsTask())
        };
        await WaitUntilAsync(() => coordinator.QueuedGlobalForTesting == waiters.Length);

        first.Dispose();
        var order = new List<string>();
        var remaining = waiters.ToList();
        for (var index = 0; index < 4; index++)
        {
            var completed = await Task.WhenAny(remaining.Select(item => item.Task)).WaitAsync(TimeSpan.FromSeconds(2));
            var selected = remaining.Single(item => ReferenceEquals(item.Task, completed));
            using var lease = await completed;
            order.Add(selected.Name);
            remaining.Remove(selected);
        }

        Assert.Equal(["interactive-1", "interactive-2", "interactive-3", "background"], order);
    }

    [Theory]
    [InlineData(MoodleAdmissionPriority.Interactive)]
    [InlineData(MoodleAdmissionPriority.Background)]
    public async Task Uses_available_capacity_when_only_one_priority_class_has_waiters(MoodleAdmissionPriority priority)
    {
        using var coordinator = CreateCoordinator(maxGlobal: 1, maxInstallation: 1, maxConnection: 1);
        var active = await coordinator.AcquireAsync(Context(), default);
        var waiters = Enumerable.Range(0, 3)
            .Select(_ => coordinator.AcquireAsync(Context(priority: priority), default).AsTask())
            .ToList();
        await WaitUntilAsync(() => coordinator.QueuedGlobalForTesting == waiters.Count);

        active.Dispose();
        while (waiters.Count > 0)
        {
            var completed = await Task.WhenAny(waiters).WaitAsync(TimeSpan.FromSeconds(2));
            using var lease = await completed;
            Assert.Equal(1, coordinator.ActiveGlobalForTesting);
            waiters.Remove(completed);
        }

        Assert.Equal(0, coordinator.QueuedGlobalForTesting);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Mixed_portal_mcp_and_background_producers_share_process_and_installation_caps_under_synthetic_load()
    {
        using var coordinator = CreateCoordinator(maxGlobal: 4, maxInstallation: 2, maxConnection: 1);
        var producers = new[]
        {
            new Producer("owner-portal", "connection-portal", "moodle-shared", MoodleAdmissionOrigin.Portal, MoodleAdmissionPriority.Interactive),
            new Producer("owner-mcp", "connection-mcp", "moodle-shared", MoodleAdmissionOrigin.Mcp, MoodleAdmissionPriority.Interactive),
            new Producer("owner-snapshot", "connection-snapshot", "moodle-shared", MoodleAdmissionOrigin.Snapshot, MoodleAdmissionPriority.Background),
            new Producer("owner-report", "connection-report", "moodle-shared", MoodleAdmissionOrigin.Report, MoodleAdmissionPriority.Background),
            new Producer("owner-grading", "connection-grading", "moodle-shared", MoodleAdmissionOrigin.Grading, MoodleAdmissionPriority.Background),
            new Producer("owner-other", "connection-other", "moodle-other", MoodleAdmissionOrigin.Portal, MoodleAdmissionPriority.Interactive)
        };

        var activeGlobal = 0;
        var activeSharedInstallation = 0;
        var activeOtherInstallation = 0;
        var maxGlobal = 0;
        var maxSharedInstallation = 0;
        var maxOtherInstallation = 0;
        var sends = 0;

        async Task RunProducerAsync(Producer producer)
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                using var lease = await coordinator.AcquireAsync(
                    Context(
                        owner: producer.Owner,
                        connection: producer.Connection,
                        installation: producer.Installation,
                        priority: producer.Priority,
                        origin: producer.Origin),
                    default).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

                Assert.InRange(coordinator.ActiveGlobalForTesting, 1, 4);
                Assert.InRange(coordinator.ActiveInstallationForTesting(producer.Installation), 1, 2);
                Assert.Equal(1, coordinator.ActiveConnectionForTesting(producer.Owner, producer.Connection));
                Assert.True(lease.TryStartAttempt());
                UpdateMaximum(ref maxGlobal, Interlocked.Increment(ref activeGlobal));
                if (producer.Installation == "moodle-shared")
                    UpdateMaximum(ref maxSharedInstallation, Interlocked.Increment(ref activeSharedInstallation));
                else
                    UpdateMaximum(ref maxOtherInstallation, Interlocked.Increment(ref activeOtherInstallation));

                try
                {
                    Interlocked.Increment(ref sends);
                    await Task.Delay(1);
                }
                finally
                {
                    Interlocked.Decrement(ref activeGlobal);
                    if (producer.Installation == "moodle-shared")
                        Interlocked.Decrement(ref activeSharedInstallation);
                    else
                        Interlocked.Decrement(ref activeOtherInstallation);
                }
            }
        }

        await Task.WhenAll(producers.Select(RunProducerAsync));

        Assert.Equal(producers.Length * 30, sends);
        Assert.InRange(maxGlobal, 1, 4);
        Assert.InRange(maxSharedInstallation, 1, 2);
        Assert.InRange(maxOtherInstallation, 1, 2);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Counts_only_committed_http_sends_against_the_shared_attempt_budget()
    {
        using var coordinator = CreateCoordinator(connectionRate: 6_000, connectionBurst: 1);
        var context = Context(budget: new MoodleAttemptBudget(1));
        using (var reservation = await coordinator.AcquireAsync(context, default))
        {
            Assert.True(reservation.TryStartAttempt());
        }
        Assert.Equal(1, context.AttemptBudget.AttemptsUsed);

        using var retryReservation = await coordinator.AcquireAsync(context, default);
        Assert.False(retryReservation.TryStartAttempt());
        Assert.Equal(1, context.AttemptBudget.AttemptsUsed);
    }

    [Fact]
    public async Task Admits_token_requests_as_separate_attempts_without_holding_a_read_lease_before_token_acquisition()
    {
        using var coordinator = CreateCoordinator();
        var context = Context(operation: MoodleAdmissionOperation.Token);

        using var tokenLease = await coordinator.AcquireAsync(context, default);
        Assert.True(tokenLease.TryStartAttempt());
        Assert.Equal(1, context.AttemptBudget.AttemptsUsed);
        Assert.Equal(1, coordinator.ActiveGlobalForTesting);
    }

    [Theory]
    [InlineData(MoodleAdmissionOperation.Write, 1)]
    [InlineData(MoodleAdmissionOperation.Download, 3)]
    public async Task Admits_write_and_download_operations_under_their_attempt_budgets(
        MoodleAdmissionOperation operation,
        int maximumAttempts)
    {
        using var coordinator = CreateCoordinator();
        var context = Context(operation: operation, budget: new MoodleAttemptBudget(maximumAttempts));

        using (var lease = await coordinator.AcquireAsync(context, default))
        {
            Assert.True(lease.TryStartAttempt());
            Assert.Equal(1, context.AttemptBudget.AttemptsUsed);
        }

        Assert.Equal(0, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Refunds_rate_reservation_when_no_http_send_starts()
    {
        using var coordinator = CreateCoordinator(connectionRate: 6_000, connectionBurst: 1);
        var context = Context(budget: new MoodleAttemptBudget(1));
        using (await coordinator.AcquireAsync(context, default)) { }

        using var sent = await coordinator.AcquireAsync(context, default);
        Assert.True(sent.TryStartAttempt());
        Assert.Equal(1, context.AttemptBudget.AttemptsUsed);
    }

    [Fact]
    public async Task Waits_for_rate_refill_without_reserving_a_concurrency_slot()
    {
        using var coordinator = CreateCoordinator(connectionRate: 60, connectionBurst: 1);
        var first = Context();
        using (var lease = await coordinator.AcquireAsync(first, default))
            Assert.True(lease.TryStartAttempt());

        var second = coordinator.AcquireAsync(Context(), default).AsTask();
        await WaitUntilAsync(() => coordinator.QueuedGlobalForTesting == 1);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        Assert.Equal(0, coordinator.ActiveGlobalForTesting);

        using var resumed = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, coordinator.ActiveGlobalForTesting);
    }

    [Fact]
    public async Task Applies_the_longest_connection_cooldown_and_keeps_another_connection_eligible()
    {
        using var coordinator = CreateCoordinator(maxGlobal: 2, maxInstallation: 2, maxConnection: 1);
        var connectionA = Context(connection: "connection-a");
        coordinator.RecordRateLimit(connectionA, TimeSpan.FromMilliseconds(120));
        coordinator.RecordRateLimit(connectionA, TimeSpan.FromMilliseconds(220));
        var delayed = coordinator.AcquireAsync(connectionA, default).AsTask();

        using var independent = await coordinator.AcquireAsync(
            Context(connection: "connection-b"), default).AsTask().WaitAsync(TimeSpan.FromMilliseconds(100));
        Assert.False(delayed.IsCompleted);

        using var resumed = await delayed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, coordinator.QueuedGlobalForTesting);
    }

    [Fact]
    public async Task Installation_cooldown_blocks_connections_to_that_installation_but_not_another()
    {
        using var coordinator = CreateCoordinator(maxGlobal: 2, maxInstallation: 2, maxConnection: 1);
        coordinator.RecordRateLimit(Context(connection: "connection-a"), TimeSpan.FromMilliseconds(180), MoodleCooldownScope.Installation);

        var sameInstallation = coordinator.AcquireAsync(Context(connection: "connection-b"), default).AsTask();
        using var otherInstallation = await coordinator.AcquireAsync(Context(connection: "connection-c", installation: "moodle-b"), default)
            .AsTask().WaitAsync(TimeSpan.FromMilliseconds(100));

        Assert.False(sameInstallation.IsCompleted);
        using var resumed = await sameInstallation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Operation_deadline_does_not_shorten_a_longer_connection_cooldown()
    {
        var start = UtcNowRoundedToSecond();
        using var clock = new ManualTimeProvider(start);
        using var coordinator = CreateCoordinator(timeProvider: clock, queueWaitSeconds: 60);
        coordinator.RecordRateLimit(Context(now: start), TimeSpan.FromSeconds(60));

        var shortBudget = new MoodleAttemptBudget(2);
        var shortWait = coordinator.AcquireAsync(
            Context(budget: shortBudget, now: start, deadline: TimeSpan.FromSeconds(10)), default).AsTask();
        Assert.Equal(1, coordinator.QueuedGlobalForTesting);
        clock.Advance(TimeSpan.FromSeconds(10));

        var deadline = await Assert.ThrowsAsync<MoodleAdmissionException>(() => shortWait);
        Assert.Equal("moodle_admission_deadline", deadline.ErrorCode);
        Assert.Equal(0, shortBudget.AttemptsUsed);

        var laterBudget = new MoodleAttemptBudget(2);
        var laterWait = coordinator.AcquireAsync(
            Context(budget: laterBudget, now: clock.GetUtcNow(), deadline: TimeSpan.FromSeconds(60)), default).AsTask();
        Assert.Equal(1, coordinator.QueuedGlobalForTesting);
        clock.Advance(TimeSpan.FromSeconds(49));
        Assert.False(laterWait.IsCompleted);
        Assert.Equal(0, laterBudget.AttemptsUsed);

        clock.Advance(TimeSpan.FromSeconds(1));
        using var resumed = await laterWait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(resumed.TryStartAttempt());
        Assert.Equal(1, laterBudget.AttemptsUsed);
    }

    [Fact]
    public async Task Rejects_new_partition_state_when_configured_capacity_is_reached()
    {
        using var coordinator = new MoodleAdmissionCoordinator(Options.Create(new MoodleAdmissionOptions
        {
            MaxConcurrentGlobal = 2,
            MaxConcurrentPerInstallation = 2,
            MaxConcurrentPerConnection = 1,
            QueueLimitGlobal = 5,
            QueueLimitPerInstallation = 5,
            QueueLimitPerConnection = 5,
            QueueWaitTimeoutSeconds = 2,
            MaximumTrackedConnections = 1,
            MaximumTrackedInstallations = 1
        }), TimeProvider.System);
        using var first = await coordinator.AcquireAsync(Context(), default);

        var error = await Assert.ThrowsAsync<MoodleAdmissionException>(async () =>
            await coordinator.AcquireAsync(Context(connection: "connection-b"), default));

        Assert.Equal("moodle_admission_state_capacity", error.ErrorCode);
    }

    [Fact]
    public async Task Idle_partitions_are_removed_after_the_last_lease_releases_but_active_partitions_are_kept()
    {
        var start = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        using var clock = new ManualTimeProvider(start);
        using var coordinator = new MoodleAdmissionCoordinator(Options.Create(new MoodleAdmissionOptions
        {
            MaxConcurrentGlobal = 2,
            MaxConcurrentPerInstallation = 2,
            MaxConcurrentPerConnection = 1,
            QueueLimitGlobal = 5,
            QueueLimitPerInstallation = 5,
            QueueLimitPerConnection = 5,
            QueueWaitTimeoutSeconds = 2,
            MaximumTrackedConnections = 1,
            MaximumTrackedInstallations = 1,
            IdlePartitionExpirationMinutes = 1
        }), clock);
        var active = await coordinator.AcquireAsync(Context(now: start), default);

        clock.Advance(TimeSpan.FromMinutes(2));
        var capacityError = await Assert.ThrowsAsync<MoodleAdmissionException>(async () =>
            await coordinator.AcquireAsync(Context(connection: "connection-b", now: clock.GetUtcNow()), default));
        Assert.Equal("moodle_admission_state_capacity", capacityError.ErrorCode);

        active.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
        using var replacement = await coordinator.AcquireAsync(
            Context(connection: "connection-b", now: clock.GetUtcNow()), default)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, coordinator.ActiveGlobalForTesting);
        Assert.Equal(1, coordinator.ActiveConnectionForTesting("owner-1", "connection-b"));
    }

    [Fact]
    public async Task Cancellation_removes_a_waiter_and_full_queues_reject_without_consuming_an_attempt()
    {
        using var coordinator = CreateCoordinator(maxGlobal: 1, maxInstallation: 1, maxConnection: 1,
            queueGlobal: 1, queueInstallation: 1, queueConnection: 1);
        var active = await coordinator.AcquireAsync(Context(), default);
        using var cancellation = new CancellationTokenSource();
        var cancelled = coordinator.AcquireAsync(Context(connection: "connection-b"), cancellation.Token).AsTask();
        await WaitUntilAsync(() => coordinator.QueuedGlobalForTesting == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, coordinator.QueuedGlobalForTesting);

        var queued = coordinator.AcquireAsync(Context(connection: "connection-c"), default).AsTask();
        await WaitUntilAsync(() => coordinator.QueuedGlobalForTesting == 1);
        var budget = new MoodleAttemptBudget(1);
        var full = await Assert.ThrowsAsync<MoodleAdmissionException>(async () =>
            await coordinator.AcquireAsync(Context(connection: "connection-d", budget: budget), default));
        Assert.Equal("moodle_admission_queue_full", full.ErrorCode);
        Assert.Equal(0, budget.AttemptsUsed);

        active.Dispose();
        using var released = await queued.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Queue_wait_timeout_removes_the_waiter_without_consuming_an_attempt()
    {
        var start = UtcNowRoundedToSecond();
        using var clock = new ManualTimeProvider(start);
        using var coordinator = CreateCoordinator(
            maxGlobal: 1,
            maxInstallation: 1,
            maxConnection: 1,
            queueWaitSeconds: 1,
            timeProvider: clock);
        var active = await coordinator.AcquireAsync(Context(now: start), default);
        var budget = new MoodleAttemptBudget(1);
        var wait = coordinator.AcquireAsync(
            Context(connection: "connection-b", budget: budget, now: start, deadline: TimeSpan.FromSeconds(5)), default)
            .AsTask();
        Assert.Equal(1, coordinator.QueuedGlobalForTesting);

        clock.Advance(TimeSpan.FromSeconds(1));

        var timeout = await Assert.ThrowsAsync<MoodleAdmissionException>(() => wait);
        Assert.Equal("moodle_admission_queue_timeout", timeout.ErrorCode);
        Assert.Equal(0, coordinator.QueuedGlobalForTesting);
        Assert.Equal(1, coordinator.ActiveGlobalForTesting);
        Assert.Equal(0, budget.AttemptsUsed);

        active.Dispose();
        using var replacement = await coordinator.AcquireAsync(
            Context(connection: "connection-b", budget: budget, now: clock.GetUtcNow()), default);
        Assert.Equal(0, coordinator.QueuedGlobalForTesting);
        Assert.True(replacement.TryStartAttempt());
        Assert.Equal(1, budget.AttemptsUsed);
    }

    [Fact]
    public async Task Shutdown_completes_queued_waiters_and_rejects_new_admissions()
    {
        using var coordinator = CreateCoordinator(maxGlobal: 1, maxInstallation: 1, maxConnection: 1);
        var active = await coordinator.AcquireAsync(Context(), default);
        var queued = coordinator.AcquireAsync(Context(connection: "connection-b"), default).AsTask();
        await WaitUntilAsync(() => coordinator.QueuedGlobalForTesting == 1);

        coordinator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        Assert.Equal(0, coordinator.QueuedGlobalForTesting);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await coordinator.AcquireAsync(Context(connection: "connection-c"), default));
        active.Dispose();
    }

    private static MoodleAdmissionCoordinator CreateCoordinator(
        int maxGlobal = 4,
        int maxInstallation = 2,
        int maxConnection = 1,
        int queueGlobal = 20,
        int queueInstallation = 10,
        int queueConnection = 5,
        int? connectionRate = null,
        int? connectionBurst = null,
        TimeProvider? timeProvider = null,
        int queueWaitSeconds = 2) =>
        new(Options.Create(new MoodleAdmissionOptions
        {
            MaxConcurrentGlobal = maxGlobal,
            MaxConcurrentPerInstallation = maxInstallation,
            MaxConcurrentPerConnection = maxConnection,
            QueueLimitGlobal = queueGlobal,
            QueueLimitPerInstallation = queueInstallation,
            QueueLimitPerConnection = queueConnection,
            QueueWaitTimeoutSeconds = queueWaitSeconds,
            MaximumTrackedConnections = 20,
            MaximumTrackedInstallations = 10,
            PermitsPerMinutePerConnection = connectionRate,
            ConnectionBurst = connectionBurst
        }), timeProvider ?? TimeProvider.System);

    private static MoodleAdmissionContext Context(
        string owner = "owner-1",
        string connection = "connection-a",
        string installation = "moodle-a",
        MoodleAdmissionOrigin origin = MoodleAdmissionOrigin.Portal,
        MoodleAdmissionOperation operation = MoodleAdmissionOperation.Read,
        MoodleAdmissionPriority priority = MoodleAdmissionPriority.Interactive,
        MoodleAttemptBudget? budget = null,
        DateTimeOffset? now = null,
        TimeSpan? deadline = null) =>
        new(owner, connection, installation, origin, operation,
            (now ?? DateTimeOffset.UtcNow).Add(deadline ?? TimeSpan.FromSeconds(5)), budget ?? new MoodleAttemptBudget(3), priority);

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private sealed record Producer(
        string Owner,
        string Connection,
        string Installation,
        MoodleAdmissionOrigin Origin,
        MoodleAdmissionPriority Priority);

    private static DateTimeOffset UtcNowRoundedToSecond()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Admission condition was not reached.");
            await Task.Delay(5);
        }
    }
}
