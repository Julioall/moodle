using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Infrastructure;
using Xunit;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleSnapshotCacheLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedExpirationCallbackMustNotEvictRefreshedSnapshot(bool releaseDuringPublish)
    {
        var owner = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ConnectorDbContext(options);
        db.MoodleSnapshots.Add(new MoodleSnapshotEntity
        {
            Id = Guid.NewGuid(), OwnerId = owner, ConnectionId = "", ConnectionAlias = "review",
            SnapshotType = "courses", CourseId = "", PayloadJson = "[\"value\"]", Tier = "warm",
            UpdatedAt = DateTimeOffset.UtcNow, FreshUntil = DateTimeOffset.UtcNow.AddHours(1),
            StaleUntil = DateTimeOffset.UtcNow.AddHours(2)
        });
        await db.SaveChangesAsync();
        var clock = new TestClock();
        using var cache = new InspectableCache(clock);
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);
        Assert.NotNull(await store.GetAsync<string[]>(owner, "review", "courses"));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var oldEntry = cache.LastEntry!;
        var registration = Assert.Single(oldEntry.PostEvictionCallbacks);
        var original = registration.EvictionCallback!;
        registration.EvictionCallback = (key, value, reason, state) =>
        {
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Review callback gate timed out.");
                original(key, value, reason, state);
                finished.TrySetResult();
            }
            catch (Exception ex) { finished.TrySetException(ex); }
        };

        try
        {
            clock.UtcNow += TimeSpan.FromSeconds(16);
            Assert.False(cache.TryGetValue(oldEntry.Key, out _));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (releaseDuringPublish)
            {
                cache.BeforeCreate = () =>
                {
                    release.Set();
                    finished.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                };
            }
            Assert.NotNull(await store.GetAsync<string[]>(owner, "review", "courses"));
            var newKey = cache.LastEntry!.Key;
            Assert.True(cache.TryGetValue(newKey, out _));
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(cache.TryGetValue(newKey, out _),
                "The expired entry's delayed callback canceled the generation shared by the refreshed entry.");

            // Final expiration still releases all metadata for this owner.
            var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastCallback = Assert.Single(cache.LastEntry!.PostEvictionCallbacks);
            var cleanup = lastCallback.EvictionCallback!;
            lastCallback.EvictionCallback = (key, value, reason, state) =>
            {
                cleanup(key, value, reason, state);
                cleaned.TrySetResult();
            };
            clock.UtcNow += TimeSpan.FromSeconds(16);
            Assert.False(cache.TryGetValue(newKey, out _));
            await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var metadata = (System.Collections.IDictionary)typeof(MoodleSnapshotStore)
                .GetField("CacheVersions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null)!;
            Assert.DoesNotContain(metadata.Keys.Cast<string>(), key => key.StartsWith(owner.ToString("N") + ":", StringComparison.Ordinal));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task InvalidationDuringPublishPreventsStaleEntryFromReturningToCache()
    {
        var owner = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ConnectorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ConnectorDbContext(options);
        using var cache = new InspectableCache(new TestClock());
        using var metrics = new MoodleSnapshotMetrics();
        var store = new MoodleSnapshotStore(db, cache, metrics, NullLogger<MoodleSnapshotStore>.Instance);
        await store.SaveAsync(owner, "review", "courses", "", new[] { "value" }, "warm", false, true, 1, DateTimeOffset.UtcNow);
        cache.BeforeCreate = () => store.Invalidate(owner, "review", "courses");
        Assert.NotNull(await store.GetAsync<string[]>(owner, "review", "courses"));
        var invalidatedKey = cache.LastEntry!.Key;
        Assert.False(cache.TryGetValue(invalidatedKey, out _));

        Assert.NotNull(await store.GetAsync<string[]>(owner, "review", "courses"));
        Assert.NotEqual(invalidatedKey, cache.LastEntry!.Key);
        Assert.True(cache.TryGetValue(cache.LastEntry.Key, out _));
    }

#pragma warning disable CS0618
    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class InspectableCache(TestClock clock) : IMemoryCache
    {
        private readonly MemoryCache inner = new(new MemoryCacheOptions { Clock = clock });
        public ICacheEntry? LastEntry { get; private set; }
        public Action? BeforeCreate { get; set; }
        public ICacheEntry CreateEntry(object key)
        {
            var action = BeforeCreate;
            BeforeCreate = null;
            action?.Invoke();
            return LastEntry = inner.CreateEntry(key);
        }
        public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);
        public void Remove(object key) => inner.Remove(key);
        public void Dispose() => inner.Dispose();
    }
#pragma warning restore CS0618
}
