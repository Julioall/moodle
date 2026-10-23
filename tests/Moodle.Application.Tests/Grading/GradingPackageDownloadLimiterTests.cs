using Moodle.Application.Grading.Packages;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPackageDownloadLimiterTests
{
    [Fact]
    public async Task AcquireAsync_limits_parallelism_per_connection_without_blocking_other_connections()
    {
        var limiter = new GradingPackageDownloadLimiter();

        using var first = await limiter.AcquireAsync(
            "connection-a",
            maxConcurrency: 1,
            CancellationToken.None);
        var sameConnectionWaiter = limiter.AcquireAsync(
            "connection-a",
            maxConcurrency: 1,
            CancellationToken.None).AsTask();

        Assert.False(sameConnectionWaiter.IsCompleted);

        using (await limiter.AcquireAsync(
                   "connection-b",
                   maxConcurrency: 1,
                   CancellationToken.None))
        {
        }

        first.Dispose();
        using var second = await sameConnectionWaiter.WaitAsync(TimeSpan.FromSeconds(1));
        second.Dispose();

        Assert.Equal(0, limiter.EntryCount);
    }

    [Fact]
    public async Task AcquireAsync_releases_a_cancelled_waiter_without_poisoning_the_gate()
    {
        var limiter = new GradingPackageDownloadLimiter();
        using var first = await limiter.AcquireAsync(
            "connection-a",
            maxConcurrency: 1,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiter = limiter.AcquireAsync(
            "connection-a",
            maxConcurrency: 1,
            cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        first.Dispose();

        Assert.Equal(0, limiter.EntryCount);
    }
}
