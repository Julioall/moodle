using System.Security.Cryptography;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleResourceContentCacheTests
{
    [Fact]
    public async Task Same_resource_concurrent_first_reads_share_one_download()
    {
        using var cache = new MoodleResourceContentCache(TimeProvider.System, sizeLimitBytes: 64, maxEntryBytes: 32);
        var bytes = "synthetic"u8.ToArray();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<SubmissionFileDownloadResult> Download(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Result(bytes);
        }

        var firstTask = ReadAsync(cache, "resource-a", Download);
        await started.Task;
        var secondTask = ReadAsync(cache, "resource-a", Download);
        await WaitUntilAsync(() => cache.ActiveFlightWaiterCountForTesting("resource-a") == 2);
        release.TrySetResult();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, calls);
        Assert.Equal(results[0].Content, results[1].Content);
        Assert.Equal(bytes, results[0].Content);
        Assert.Equal(bytes.Length, cache.CurrentSizeBytes);
    }

    [Fact]
    public async Task Cancelling_first_waiter_keeps_shared_download_alive_for_remaining_waiter()
    {
        using var cache = new MoodleResourceContentCache(TimeProvider.System, sizeLimitBytes: 64, maxEntryBytes: 32);
        using var firstCancellation = new CancellationTokenSource();
        var bytes = "synthetic"u8.ToArray();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCancelled = false;
        var calls = 0;

        async Task<SubmissionFileDownloadResult> Download(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            using var registration = cancellationToken.Register(() => downloadCancelled = true);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Result(bytes);
        }

        var firstTask = ReadAsync(cache, "resource-cancel-one", Download, cancellationToken: firstCancellation.Token);
        await started.Task;
        var secondTask = ReadAsync(cache, "resource-cancel-one", Download);
        await WaitUntilAsync(() => cache.ActiveFlightWaiterCountForTesting("resource-cancel-one") == 2);

        firstCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask);
        Assert.False(downloadCancelled);
        release.TrySetResult();
        var second = await secondTask;

        Assert.Equal(1, calls);
        Assert.Equal(bytes, second.Content);
    }

    [Fact]
    public async Task Oversized_entry_is_delivered_without_being_cached()
    {
        using var cache = new MoodleResourceContentCache(TimeProvider.System, sizeLimitBytes: 20, maxEntryBytes: 4);
        var bytes = new byte[5];

        var result = await ReadAsync(cache, "resource-large", _ => Task.FromResult(Result(bytes)), maxBytes: 20);

        Assert.Equal(bytes, result.Content);
        Assert.Equal(0, cache.CurrentSizeBytes);
        Assert.Equal(0, cache.EntryCountForTesting);
        Assert.Equal(128L * 1024 * 1024, MoodleResourceContentCache.DefaultSizeLimitBytes);
        Assert.Equal(25L * 1024 * 1024, MoodleResourceContentCache.DefaultMaxEntryBytes);
    }

    [Fact]
    public async Task Cache_evicts_least_recently_used_entries_without_exceeding_byte_budget()
    {
        using var cache = new MoodleResourceContentCache(TimeProvider.System, sizeLimitBytes: 10, maxEntryBytes: 4);
        var firstBytes = new byte[] { 1, 11, 21, 31 };
        var secondBytes = new byte[] { 2, 12, 22, 32 };
        var thirdBytes = new byte[] { 3, 13, 23, 33 };
        var firstCalls = 0;
        var secondCalls = 0;
        var firstHash = Hash(firstBytes);
        var secondHash = Hash(secondBytes);

        await ReadAsync(cache, "resource-0", _ => { firstCalls++; return Task.FromResult(Result(firstBytes)); }, maxBytes: 8, expectedSha256: firstHash);
        await ReadAsync(cache, "resource-1", _ => { secondCalls++; return Task.FromResult(Result(secondBytes)); }, maxBytes: 8, expectedSha256: secondHash);
        await ReadAsync(cache, "resource-0", _ => { firstCalls++; return Task.FromResult(Result(firstBytes)); }, maxBytes: 8, expectedSha256: firstHash);
        await ReadAsync(cache, "resource-2", _ => Task.FromResult(Result(thirdBytes)), maxBytes: 8, expectedSha256: Hash(thirdBytes));

        Assert.Equal(8, cache.CurrentSizeBytes);
        Assert.Equal(2, cache.EntryCountForTesting);
        await ReadAsync(cache, "resource-0", _ => { firstCalls++; return Task.FromResult(Result(firstBytes)); }, maxBytes: 8, expectedSha256: firstHash);
        await ReadAsync(cache, "resource-1", _ => { secondCalls++; return Task.FromResult(Result(secondBytes)); }, maxBytes: 8, expectedSha256: secondHash);
        Assert.Equal(1, firstCalls);
        Assert.Equal(2, secondCalls);
    }

    [Fact]
    public async Task Cached_content_expires_after_shorter_of_resource_expiry_and_fifteen_minutes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
        using var cache = new MoodleResourceContentCache(clock, sizeLimitBytes: 32, maxEntryBytes: 16);
        var bytes = "ttl"u8.ToArray();
        var calls = 0;
        Task<SubmissionFileDownloadResult> Download(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Result(bytes));
        }

        await ReadAsync(cache, "resource-ttl", Download, resourceExpiresAt: clock.GetUtcNow().AddHours(1));
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(0, cache.CurrentSizeBytes);
        await ReadAsync(cache, "resource-ttl", Download, resourceExpiresAt: clock.GetUtcNow().AddHours(1));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Resource_expiration_shortens_the_cache_ttl()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
        using var cache = new MoodleResourceContentCache(clock, sizeLimitBytes: 32, maxEntryBytes: 16);
        var bytes = "short"u8.ToArray();
        var hash = Hash(bytes);
        var calls = 0;
        Task<SubmissionFileDownloadResult> Download(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Result(bytes));
        }

        await ReadAsync(cache, "resource-expiring", Download, expectedSha256: hash, resourceExpiresAt: clock.GetUtcNow().AddMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(0, cache.CurrentSizeBytes);
        await ReadAsync(cache, "resource-expiring", Download, expectedSha256: hash, resourceExpiresAt: clock.GetUtcNow().AddMinutes(5));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cache_key_is_scoped_to_client_and_connection()
    {
        using var cache = new MoodleResourceContentCache(TimeProvider.System, sizeLimitBytes: 32, maxEntryBytes: 16);
        var bytes = "same bytes"u8.ToArray();
        var hash = Hash(bytes);
        var calls = 0;
        var first = await ReadAsync(cache, "resource-scope-a", _ => { calls++; return Task.FromResult(Result(bytes)); }, expectedSha256: hash);
        var second = await cache.GetOrDownloadAsync(
            "synthetic-client-other",
            "synthetic-connection-other",
            "resource-scope-b",
            hash,
            DateTimeOffset.UtcNow.AddHours(1),
            1024,
            _ => { calls++; return Task.FromResult(Result(bytes)); },
            CancellationToken.None);

        Assert.Equal(MoodleResourceContentSource.Downloaded, first.Source);
        Assert.Equal(MoodleResourceContentSource.Downloaded, second.Source);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cache_hit_returns_isolated_bytes_and_cannot_be_mutated_by_a_reader()
    {
        using var cache = new MoodleResourceContentCache(TimeProvider.System, sizeLimitBytes: 64, maxEntryBytes: 32);
        var bytes = "immutable cache copy"u8.ToArray();
        var hash = Hash(bytes);
        var first = await ReadAsync(cache, "resource-copy-a", _ => Task.FromResult(Result(bytes)), expectedSha256: hash);
        first.Content[0] = 0;
        var second = await ReadAsync(cache, "resource-copy-b", _ => throw new InvalidOperationException("Cache miss should not invoke downloader."), expectedSha256: hash);

        Assert.Equal((byte)'i', second.Content[0]);
        Assert.NotSame(first.Content, second.Content);
    }

    [Fact]
    public async Task Failure_truncation_hash_mismatch_and_cancellation_leave_no_cache_or_stuck_flight()
    {
        using var cache = new MoodleResourceContentCache(TimeProvider.System, sizeLimitBytes: 32, maxEntryBytes: 16);
        var bytes = "valid"u8.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(
            cache, "resource-failure", _ => Task.FromException<SubmissionFileDownloadResult>(new InvalidOperationException("synthetic failure"))));
        Assert.Equal(0, cache.CurrentSizeBytes);

        await Assert.ThrowsAsync<MoodleResourceException>(() => ReadAsync(
            cache, "resource-truncated", _ => Task.FromResult(Result(bytes) with { Truncated = true })));
        await Assert.ThrowsAsync<MoodleResourceException>(() => ReadAsync(
            cache, "resource-hash", _ => Task.FromResult(Result(bytes) with { Sha256Hex = new string('0', 64) })));
        await Assert.ThrowsAsync<MoodleResourceException>(() => ReadAsync(
            cache, "resource-size", _ => Task.FromResult(Result(bytes) with { SizeBytes = bytes.LongLength + 1 })));
        Assert.Equal(0, cache.CurrentSizeBytes);

        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledRead = ReadAsync(
            cache,
            "resource-cancelled",
            async token =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Result(bytes);
            },
            cancellationToken: cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead);
        Assert.Equal(0, cache.CurrentSizeBytes);

        var recovered = await ReadAsync(cache, "resource-cancelled", _ => Task.FromResult(Result(bytes)));
        Assert.Equal(bytes, recovered.Content);
        Assert.Equal(bytes.Length, cache.CurrentSizeBytes);
    }

    [Fact]
    public async Task Known_hash_is_checked_against_actual_content_before_caching()
    {
        using var cache = new MoodleResourceContentCache(TimeProvider.System, sizeLimitBytes: 32, maxEntryBytes: 16);
        var bytes = "synthetic"u8.ToArray();
        var badHash = new string('f', 64);

        await Assert.ThrowsAsync<MoodleResourceException>(() => ReadAsync(
            cache,
            "resource-expected-hash",
            _ => Task.FromResult(Result(bytes)),
            expectedSha256: badHash));

        Assert.Equal(0, cache.CurrentSizeBytes);
        var corrected = await ReadAsync(cache, "resource-expected-hash", _ => Task.FromResult(Result(bytes)));
        Assert.Equal(bytes, corrected.Content);
    }

    private static Task<MoodleResourceContent> ReadAsync(
        MoodleResourceContentCache cache,
        string resourceId,
        Func<CancellationToken, Task<SubmissionFileDownloadResult>> downloader,
        long maxBytes = 1024,
        string? expectedSha256 = null,
        DateTimeOffset? resourceExpiresAt = null,
        CancellationToken cancellationToken = default,
        string clientId = "synthetic-client",
        string connectionId = "synthetic-connection") =>
        cache.GetOrDownloadAsync(
            clientId,
            connectionId,
            resourceId,
            expectedSha256,
            resourceExpiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
            maxBytes,
            downloader,
            cancellationToken);

    private static SubmissionFileDownloadResult Result(byte[] content) => new(
        "synthetic.txt",
        "text/plain",
        content.LongLength,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
        content,
        false);

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Wait condition was not met.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset now = utcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan delta) => now += delta;
    }
}
