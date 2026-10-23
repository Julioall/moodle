using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;
using Moodle.Infrastructure.MoodleApi;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleReadConcurrencyGateTests
{
    [Fact]
    public async Task Gate_limits_active_reads_and_rejects_a_full_per_connection_queue()
    {
        using var gate = CreateGate(new MoodleReadLimitsOptions
        {
            Enabled = true,
            MaxConcurrentPerConnection = 1,
            QueueLimitPerConnection = 1,
            QueueWaitTimeoutSeconds = 2,
            MaxTrackedConnections = 10
        });
        var first = await gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None);
        var secondTask = gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None);
        await WaitUntilAsync(() => gate.QueuedCountForTesting("connection-a") == 1);

        var fullQueue = await Assert.ThrowsAsync<MoodleReadGateException>(
            () => gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None));

        Assert.Equal("moodle_read_queue_full", fullQueue.ErrorCode);
        first.Dispose();
        using var second = await secondTask;
        Assert.Equal(1, gate.ActiveCountForTesting("connection-a"));
        using var otherConnection = await gate.AcquireAsync("connection-b", "core_course_get_contents", CancellationToken.None);
        Assert.Equal(1, gate.ActiveCountForTesting("connection-a"));
        Assert.Equal(1, gate.ActiveCountForTesting("connection-b"));
    }

    [Fact]
    public async Task Gate_timeout_and_cancellation_release_queue_slots()
    {
        using var gate = CreateGate(new MoodleReadLimitsOptions
        {
            Enabled = true,
            MaxConcurrentPerConnection = 1,
            QueueLimitPerConnection = 2,
            QueueWaitTimeoutSeconds = 1,
            MaxTrackedConnections = 10
        });
        var first = await gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var canceledWait = gate.AcquireAsync("connection-a", "core_course_get_contents", cancellation.Token);
        await WaitUntilAsync(() => gate.QueuedCountForTesting("connection-a") == 1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledWait);
        Assert.Equal(0, gate.QueuedCountForTesting("connection-a"));
        first.Dispose();
        using var next = await gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None);
        Assert.Equal(1, gate.ActiveCountForTesting("connection-a"));
    }

    [Fact]
    public async Task Gate_times_out_a_waiter_and_releases_the_tracked_connection()
    {
        using var gate = CreateGate(new MoodleReadLimitsOptions
        {
            Enabled = true,
            MaxConcurrentPerConnection = 1,
            QueueLimitPerConnection = 1,
            QueueWaitTimeoutSeconds = 1,
            MaxTrackedConnections = 10
        });
        using var first = await gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None);

        var timeout = await Assert.ThrowsAsync<MoodleReadGateException>(
            () => gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None));

        Assert.Equal("moodle_read_queue_timeout", timeout.ErrorCode);
        Assert.Equal(0, gate.QueuedCountForTesting("connection-a"));
        Assert.Equal(1, gate.TrackedConnectionCountForTesting);
    }

    [Fact]
    public async Task Gate_limits_twenty_concurrent_acquisitions_and_tracks_connections_with_a_bound()
    {
        using var gate = CreateGate(new MoodleReadLimitsOptions
        {
            Enabled = true,
            MaxConcurrentPerConnection = 2,
            QueueLimitPerConnection = 20,
            QueueWaitTimeoutSeconds = 5,
            MaxTrackedConnections = 1
        });
        var active = 0;
        var peak = 0;
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var lease = await gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None);
            var current = Interlocked.Increment(ref active);
            UpdateMax(ref peak, current);
            await Task.Delay(10);
            Interlocked.Decrement(ref active);
        });

        await Task.WhenAll(tasks);

        Assert.Equal(2, peak);
        Assert.Equal(0, gate.TrackedConnectionCountForTesting);
        using var connection = await gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None);
        var capacity = await Assert.ThrowsAsync<MoodleReadGateException>(
            () => gate.AcquireAsync("connection-b", "core_course_get_contents", CancellationToken.None));
        Assert.Equal("moodle_read_connection_capacity", capacity.ErrorCode);
    }

    [Fact]
    public async Task Disabled_gate_does_not_allocate_a_connection_slot()
    {
        using var gate = CreateGate(new MoodleReadLimitsOptions());

        using var lease = await gate.AcquireAsync("connection-a", "core_course_get_contents", CancellationToken.None);

        Assert.Equal(0, gate.TrackedConnectionCountForTesting);
    }

    private static MoodleReadConcurrencyGate CreateGate(MoodleReadLimitsOptions options) =>
        new(Options.Create(options));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The condition was not reached before the timeout.");
            }

            await Task.Delay(5);
        }
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
