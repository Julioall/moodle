using Moodle.Application.Abstractions;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Configuration;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleSnapshotWorkSchedulerTests
{
    [Fact]
    public async Task TakeAsync_SelectsReadyConnectionBeforeEarlierBlockedWork()
    {
        var scheduler = new MoodleSnapshotWorkScheduler(new MoodleSnapshotOptions
        {
            QueueCapacity = 16,
            GlobalConcurrency = 2,
            PerConnectionConcurrency = 1,
        });
        var ownerId = Guid.NewGuid();
        var a1 = Request(ownerId, "fieg", "connection-a", "course-1");
        var a2 = Request(ownerId, "fieg", "connection-a", "course-2");
        var b1 = Request(ownerId, "senai", "connection-b", "course-3");

        Assert.True(scheduler.TryEnqueue(a1));
        Assert.True(scheduler.TryEnqueue(a2));
        Assert.True(scheduler.TryEnqueue(b1));

        Assert.Same(a1, await scheduler.TakeAsync(CancellationToken.None));
        Assert.Same(b1, await scheduler.TakeAsync(CancellationToken.None));

        scheduler.Complete(a1);
        Assert.Same(a2, await scheduler.TakeAsync(CancellationToken.None));
        scheduler.Complete(a2);
        scheduler.Complete(b1);
    }

    [Fact]
    public async Task TryEnqueue_DeduplicatesQueuedAndActiveWorkAndEnforcesCapacity()
    {
        var scheduler = new MoodleSnapshotWorkScheduler(new MoodleSnapshotOptions
        {
            QueueCapacity = 16,
            GlobalConcurrency = 1,
            PerConnectionConcurrency = 1,
        });
        var ownerId = Guid.NewGuid();
        var first = Request(ownerId, "fieg", "connection-a", "course-1");
        var second = Request(ownerId, "fieg", "connection-a", "course-2");

        Assert.True(scheduler.TryEnqueue(first));
        Assert.False(scheduler.TryEnqueue(first));
        Assert.Same(first, await scheduler.TakeAsync(CancellationToken.None));
        Assert.False(scheduler.TryEnqueue(first));
        Assert.True(scheduler.TryEnqueue(second));
        scheduler.Complete(first);
        Assert.Same(second, await scheduler.TakeAsync(CancellationToken.None));
        scheduler.Complete(second);
    }

    [Fact]
    public void TryEnqueue_RejectsWorkAfterThePendingCapacityIsReached()
    {
        var scheduler = new MoodleSnapshotWorkScheduler(new MoodleSnapshotOptions
        {
            QueueCapacity = 16,
            GlobalConcurrency = 1,
            PerConnectionConcurrency = 1,
        });
        var ownerId = Guid.NewGuid();

        for (var index = 0; index < 16; index++)
        {
            Assert.True(scheduler.TryEnqueue(Request(ownerId, "fieg", "connection-a", $"course-{index}")));
        }

        Assert.False(scheduler.TryEnqueue(Request(ownerId, "fieg", "connection-a", "course-over-capacity")));
    }

    [Fact]
    public async Task TakeAsync_CancellationReleasesAWaitingConsumer()
    {
        var scheduler = new MoodleSnapshotWorkScheduler(new MoodleSnapshotOptions());
        using var cancellation = new CancellationTokenSource();
        var waiting = scheduler.TakeAsync(cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    private static MoodleSnapshotSyncRequest Request(
        Guid ownerId,
        string alias,
        string connectionId,
        string courseId) => new(
            ownerId,
            "client-1",
            alias,
            "user-1",
            Dataset: MoodleSnapshotDatasets.Activities,
            CourseId: courseId,
            Priority: 50,
            ConnectionId: connectionId);
}
