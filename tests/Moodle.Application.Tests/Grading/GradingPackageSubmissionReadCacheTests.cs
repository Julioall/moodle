using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;
using Moodle.Application.Grading.Packages;
using Moodle.Domain;

namespace Moodle.Application.Tests.Grading;

public sealed class GradingPackageSubmissionReadCacheTests
{
    private static readonly IReadOnlyCollection<(long AssignmentId, string SubmissionId)> Required =
        [(501, "9001")];

    [Fact]
    public async Task One_hundred_requests_share_one_read_until_expiry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-23T12:00:00Z"));
        using var cache = CreateCache(clock);
        var calls = 0;

        async Task<GradingPackageSubmissionReadResult> ReadAsync() => await cache.GetOrLoadAsync(
            Scope(), 1, 1, Required, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(CompleteSnapshot());
            }, CancellationToken.None);

        var firstWindow = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => ReadAsync()));
        Assert.Equal(1, calls);
        Assert.Single(firstWindow, result => result.CacheStatus == GradingPackageSubmissionReadCacheStatus.Miss);
        Assert.Equal(99, firstWindow.Count(result => result.CacheStatus == GradingPackageSubmissionReadCacheStatus.Hit));

        clock.Advance(TimeSpan.FromSeconds(31));
        await ReadAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Owner_connection_course_and_run_are_part_of_the_key()
    {
        using var cache = CreateCache(TimeProvider.System);
        var calls = 0;
        var scopes = new[]
        {
            Scope(),
            Scope() with { OwnerSubject = "teacher-b" },
            Scope() with { CreatedByMoodleUserId = 322 },
            Scope() with { MoodleConnectionId = "connection-b" },
            Scope() with { ConnectorClientId = "client-b" },
            Scope() with { ConnectionAlias = "alias-b" },
            Scope() with { CourseId = 20 },
            Scope() with { AssignmentId = 502 },
            Scope() with { GradingScopeId = Guid.NewGuid() },
            Scope() with { IsGradingRun = false }
        };

        foreach (var scope in scopes)
        {
            await cache.GetOrLoadAsync(scope, 1, 1, Required, _ => LoadAndCount(), CancellationToken.None);
        }

        Assert.Equal(scopes.Length, calls);

        Task<GradingPackageSubmissionReadSnapshot> LoadAndCount()
        {
            calls++;
            return Task.FromResult(CompleteSnapshot());
        }
    }

    [Fact]
    public async Task Explicit_regeneration_invalidation_forces_fresh_read_even_for_version_two()
    {
        using var cache = CreateCache(TimeProvider.System);
        var calls = 0;

        var first = await cache.GetOrLoadAsync(Scope(), 1, 1, Required, _ =>
        {
            calls++;
            return Task.FromResult(CompleteSnapshot());
        }, CancellationToken.None);
        var beforeRegeneration = await cache.GetOrLoadAsync(Scope(), 2, 1, Required, _ =>
        {
            calls++;
            return Task.FromResult(CompleteSnapshot());
        }, CancellationToken.None);

        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Miss, first.CacheStatus);
        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Hit, beforeRegeneration.CacheStatus);
        cache.Invalidate(Scope());
        var regenerated = await cache.GetOrLoadAsync(Scope(), 2, 1, Required, _ =>
        {
            calls++;
            return Task.FromResult(CompleteSnapshot());
        }, CancellationToken.None);
        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Miss, regenerated.CacheStatus);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Retry_attempt_bypasses_cache()
    {
        using var cache = CreateCache(TimeProvider.System);
        var calls = 0;
        var first = await cache.GetOrLoadAsync(Scope(), 1, 2, Required, _ =>
        {
            calls++;
            return Task.FromResult(CompleteSnapshot());
        }, CancellationToken.None);
        var second = await cache.GetOrLoadAsync(Scope(), 1, 2, Required, _ =>
        {
            calls++;
            return Task.FromResult(CompleteSnapshot());
        }, CancellationToken.None);

        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Bypass, first.CacheStatus);
        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Bypass, second.CacheStatus);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Disabled_by_default_and_incomplete_or_missing_coverage_are_not_cached()
    {
        using var disabled = new GradingPackageSubmissionReadCache(
            Options.Create(new GradingLimitsOptions()), TimeProvider.System);
        var disabledCalls = 0;
        var disabledResult = await disabled.GetOrLoadAsync(Scope(), 1, 1, Required, _ =>
        {
            disabledCalls++;
            return Task.FromResult(CompleteSnapshot());
        }, CancellationToken.None);
        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Bypass, disabledResult.CacheStatus);
        await disabled.GetOrLoadAsync(Scope(), 1, 1, Required, _ =>
        {
            disabledCalls++;
            return Task.FromResult(CompleteSnapshot());
        }, CancellationToken.None);
        Assert.Equal(2, disabledCalls);

        using var cache = CreateCache(TimeProvider.System);
        var incompleteCalls = 0;
        async Task<GradingPackageSubmissionReadResult> ReadIncompleteAsync() => await cache.GetOrLoadAsync(
            Scope(), 1, 1, Required, _ =>
            {
                incompleteCalls++;
                return Task.FromResult(new GradingPackageSubmissionReadSnapshot(
                    CompleteSnapshot().Records, IsComplete: false));
            }, CancellationToken.None);

        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Bypass,
            (await ReadIncompleteAsync()).CacheStatus);
        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Bypass,
            (await ReadIncompleteAsync()).CacheStatus);
        Assert.Equal(2, incompleteCalls);

        var missingCalls = 0;
        var missingScope = Scope() with { GradingScopeId = Guid.NewGuid() };
        for (var index = 0; index < 2; index++)
        {
            var result = await cache.GetOrLoadAsync(missingScope, 1, 1,
                Required, _ =>
                {
                    missingCalls++;
                    return Task.FromResult(new GradingPackageSubmissionReadSnapshot(
                        new Dictionary<(long, string), AssignmentSubmissionRecord>(), IsComplete: true));
                }, CancellationToken.None);
            Assert.Equal(GradingPackageSubmissionReadCacheStatus.Bypass, result.CacheStatus);
        }
        Assert.Equal(2, missingCalls);
    }

    [Fact]
    public async Task Loader_failure_and_cancellation_are_not_cached()
    {
        using var cache = CreateCache(TimeProvider.System);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrLoadAsync(
            Scope(), 1, 1, Required, _ =>
            {
                calls++;
                throw new InvalidOperationException("synthetic failure");
            }, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrLoadAsync(
            Scope() with { GradingScopeId = Guid.NewGuid() }, 1, 1, Required, _ =>
            {
                calls++;
                throw new OperationCanceledException(cancellation.Token);
            }, cancellation.Token));

        var recovered = await cache.GetOrLoadAsync(Scope(), 1, 1, Required, _ =>
        {
            calls++;
            return Task.FromResult(CompleteSnapshot());
        }, CancellationToken.None);
        Assert.Equal(GradingPackageSubmissionReadCacheStatus.Miss, recovered.CacheStatus);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Oversized_online_text_is_returned_but_not_cached()
    {
        using var cache = CreateCache(TimeProvider.System);
        var calls = 0;
        var largeRecord = Record() with { OnlineText = new string('x', 32 * 1024 * 1024) };
        for (var index = 0; index < 2; index++)
        {
            var result = await cache.GetOrLoadAsync(Scope(), 1, 1, Required, _ =>
            {
                calls++;
                return Task.FromResult(new GradingPackageSubmissionReadSnapshot(
                    new Dictionary<(long, string), AssignmentSubmissionRecord> { [(501, "9001")] = largeRecord },
                    IsComplete: true));
            }, CancellationToken.None);
            Assert.Equal(GradingPackageSubmissionReadCacheStatus.Bypass, result.CacheStatus);
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Simultaneous_same_key_requests_use_single_flight()
    {
        using var cache = CreateCache(TimeProvider.System);
        var calls = 0;
        var release = new TaskCompletionSource<GradingPackageSubmissionReadSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<GradingPackageSubmissionReadSnapshot> Loader(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return release.Task;
        }

        var requests = Enumerable.Range(0, 20)
            .Select(_ => cache.GetOrLoadAsync(Scope(), 1, 1, Required, Loader, CancellationToken.None))
            .ToArray();
        while (Volatile.Read(ref calls) == 0)
        {
            await Task.Yield();
        }
        release.SetResult(CompleteSnapshot());
        var results = await Task.WhenAll(requests);

        Assert.Equal(1, calls);
        Assert.Equal(1, results.Count(result => result.CacheStatus == GradingPackageSubmissionReadCacheStatus.Miss));
        Assert.All(results, result => Assert.NotEmpty(result.Snapshot.Records));
    }

    private static GradingPackageSubmissionReadCache CreateCache(TimeProvider timeProvider) => new(
        Options.Create(new GradingLimitsOptions
        {
            GradingPackageSubmissionReuseEnabled = true,
            GradingPackageSubmissionReuseSeconds = 30,
            GradingPackageSubmissionCacheMaxBytes = 128L * 1024 * 1024
        }), timeProvider);

    private static GradingPackageSubmissionReadScope Scope() => new(
        GradingScopeId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        IsGradingRun: true,
        OwnerSubject: "teacher-a",
        CreatedByMoodleUserId: 321,
        MoodleConnectionId: "connection-a",
        ConnectorClientId: "client-a",
        ConnectionAlias: "test-alias",
        CourseId: 10,
        AssignmentId: 501);

    private static GradingPackageSubmissionReadSnapshot CompleteSnapshot() => new(
        new Dictionary<(long, string), AssignmentSubmissionRecord> { [(501, "9001")] = Record() },
        IsComplete: true);

    private static AssignmentSubmissionRecord Record() => new(
        SubmissionId: "9001",
        UserId: "101",
        Status: "submitted",
        GradingStatus: null,
        CreatedAt: null,
        ModifiedAt: null,
        AttemptNumber: 1,
        FileCount: 0,
        HasOnlineText: true,
        Files: [],
        OnlineText: "synthetic online text");

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
