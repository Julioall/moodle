using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Configuration;
using Moodle.Domain;

namespace Moodle.Application.Grading.Packages;

public sealed record GradingPackageSubmissionReadScope(
    Guid GradingScopeId,
    bool IsGradingRun,
    string OwnerSubject,
    long? CreatedByMoodleUserId,
    string? MoodleConnectionId,
    string? ConnectorClientId,
    string? ConnectionAlias,
    long CourseId,
    long AssignmentId);

public sealed record GradingPackageSubmissionReadSnapshot(
    IReadOnlyDictionary<(long AssignmentId, string SubmissionId), AssignmentSubmissionRecord> Records,
    bool IsComplete);

public enum GradingPackageSubmissionReadCacheStatus
{
    Hit,
    Miss,
    Bypass
}

public sealed record GradingPackageSubmissionReadResult(
    GradingPackageSubmissionReadSnapshot Snapshot,
    GradingPackageSubmissionReadCacheStatus CacheStatus,
    long EstimatedBytes);

/// <summary>
/// Short-lived, process-local reuse for complete Moodle assignment-submission
/// responses. The cache is deliberately opt-in and contains no persistent data.
/// </summary>
public sealed class GradingPackageSubmissionReadCache : IDisposable
{
    private const long MaxEntryBytes = 64L * 1024 * 1024;
    private const long MaxCacheBytes = 128L * 1024 * 1024;

    private readonly bool _enabled;
    private readonly TimeSpan _ttl;
    private readonly long _maxEntrySize;
    private readonly TimeProvider _timeProvider;
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<GradingPackageSubmissionReadScope, long> _invalidationGenerations = new();
    private readonly ConcurrentDictionary<CacheKey,
        Lazy<Task<GradingPackageSubmissionReadResult>>> _inFlight = new();

    public GradingPackageSubmissionReadCache(
        IOptions<GradingLimitsOptions> limits,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var options = limits.Value;
        _enabled = options.GradingPackageSubmissionReuseEnabled;
        var ttlSeconds = Math.Clamp(options.GradingPackageSubmissionReuseSeconds, 1, 30);
        _ttl = TimeSpan.FromSeconds(ttlSeconds);
        var totalLimit = Math.Clamp(options.GradingPackageSubmissionCacheMaxBytes, 1, MaxCacheBytes);
        _maxEntrySize = Math.Min(MaxEntryBytes, totalLimit);
        _timeProvider = timeProvider;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = totalLimit });
    }

    public async Task<GradingPackageSubmissionReadResult> GetOrLoadAsync(
        GradingPackageSubmissionReadScope scope,
        int packageVersion,
        int attemptCount,
        IReadOnlyCollection<(long AssignmentId, string SubmissionId)> requiredSubmissionIds,
        Func<CancellationToken, Task<GradingPackageSubmissionReadSnapshot>> loader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(requiredSubmissionIds);
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_enabled || packageVersion < 1 || attemptCount > 1)
        {
            return await LoadFreshAsync(loader, cancellationToken, GradingPackageSubmissionReadCacheStatus.Bypass);
        }

        var cacheKey = new CacheKey(scope, _invalidationGenerations.GetOrAdd(scope, 0));
        if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached is not null)
        {
            if (cached.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _cache.Remove(cacheKey);
            }
            else if (CoversRequiredSubmissions(cached.Snapshot, requiredSubmissionIds))
            {
                return new GradingPackageSubmissionReadResult(
                    cached.Snapshot,
                    GradingPackageSubmissionReadCacheStatus.Hit,
                    cached.EstimatedBytes);
            }
            else
            {
                // A package requiring a submission absent from the shared
                // response gets a fresh response. Never repeat this partial hit.
                _cache.Remove(cacheKey);
                return await LoadFreshAsync(loader, cancellationToken, GradingPackageSubmissionReadCacheStatus.Bypass);
            }
        }

        Lazy<Task<GradingPackageSubmissionReadResult>>? candidate = null;
        candidate = new Lazy<Task<GradingPackageSubmissionReadResult>>(
            () => LoadAndCacheAsync(cacheKey, candidate!, requiredSubmissionIds, loader, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = _inFlight.GetOrAdd(cacheKey, candidate);
        var isLeader = ReferenceEquals(flight, candidate);
        var result = await flight.Value.WaitAsync(cancellationToken);

        if (!CoversRequiredSubmissions(result.Snapshot, requiredSubmissionIds))
        {
            if (result.CacheStatus == GradingPackageSubmissionReadCacheStatus.Bypass)
            {
                return result;
            }

            _cache.Remove(cacheKey);
            return await LoadFreshAsync(loader, cancellationToken, GradingPackageSubmissionReadCacheStatus.Bypass);
        }

        return !isLeader && result.CacheStatus == GradingPackageSubmissionReadCacheStatus.Miss
            ? result with { CacheStatus = GradingPackageSubmissionReadCacheStatus.Hit }
            : result;
    }

    public void Invalidate(GradingPackageSubmissionReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var generation = _invalidationGenerations.AddOrUpdate(scope, 1, static (_, current) => current + 1);
        _cache.Remove(new CacheKey(scope, generation - 1));
    }

    public void Dispose() => _cache.Dispose();

    private async Task<GradingPackageSubmissionReadResult> LoadAndCacheAsync(
        CacheKey key,
        Lazy<Task<GradingPackageSubmissionReadResult>> flight,
        IReadOnlyCollection<(long AssignmentId, string SubmissionId)> requiredSubmissionIds,
        Func<CancellationToken, Task<GradingPackageSubmissionReadSnapshot>> loader,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await loader(cancellationToken);
            var estimatedBytes = EstimateBytes(snapshot.Records);
            var canCache = snapshot.IsComplete &&
                CoversRequiredSubmissions(snapshot, requiredSubmissionIds) &&
                estimatedBytes <= _maxEntrySize;
            if (!canCache || _invalidationGenerations.GetOrAdd(key.Scope, 0) != key.Generation)
            {
                return new GradingPackageSubmissionReadResult(
                    snapshot,
                    GradingPackageSubmissionReadCacheStatus.Bypass,
                    estimatedBytes);
            }

            var expiresAt = _timeProvider.GetUtcNow().Add(_ttl);
            _cache.Set(
                key,
                new CacheEntry(snapshot, estimatedBytes, expiresAt),
                new MemoryCacheEntryOptions()
                    .SetSize(Math.Max(1, estimatedBytes))
                    .SetAbsoluteExpiration(_ttl));
            return new GradingPackageSubmissionReadResult(
                snapshot,
                GradingPackageSubmissionReadCacheStatus.Miss,
                estimatedBytes);
        }
        finally
        {
            if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
            {
                _inFlight.TryRemove(key, out _);
            }
        }
    }

    private static async Task<GradingPackageSubmissionReadResult> LoadFreshAsync(
        Func<CancellationToken, Task<GradingPackageSubmissionReadSnapshot>> loader,
        CancellationToken cancellationToken,
        GradingPackageSubmissionReadCacheStatus status)
    {
        var snapshot = await loader(cancellationToken);
        return new GradingPackageSubmissionReadResult(snapshot, status, EstimateBytes(snapshot.Records));
    }

    private static bool CoversRequiredSubmissions(
        GradingPackageSubmissionReadSnapshot snapshot,
        IReadOnlyCollection<(long AssignmentId, string SubmissionId)> requiredSubmissionIds) =>
        requiredSubmissionIds.All(snapshot.Records.ContainsKey);

    private static long EstimateBytes(
        IReadOnlyDictionary<(long AssignmentId, string SubmissionId), AssignmentSubmissionRecord> records)
    {
        long bytes = 256;
        foreach (var (key, record) in records)
        {
            bytes = Add(bytes, 112);
            bytes = AddString(bytes, key.SubmissionId);
            bytes = AddString(bytes, record.SubmissionId);
            bytes = AddString(bytes, record.UserId);
            bytes = AddString(bytes, record.Status);
            bytes = AddString(bytes, record.GradingStatus);
            bytes = AddString(bytes, record.CurrentFeedback);
            bytes = AddString(bytes, record.OnlineText);
            if (record.Files is null)
            {
                continue;
            }

            foreach (var file in record.Files)
            {
                bytes = Add(bytes, 96);
                bytes = AddString(bytes, file.Filename);
                bytes = AddString(bytes, file.MimeType);
                bytes = AddString(bytes, file.FileUrl);
            }
        }

        return bytes;
    }

    private static long AddString(long current, string? value) =>
        value is null ? current : Add(current, 24L + value.Length * sizeof(char));

    private static long Add(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed record CacheEntry(
        GradingPackageSubmissionReadSnapshot Snapshot,
        long EstimatedBytes,
        DateTimeOffset ExpiresAt);

    private sealed record CacheKey(GradingPackageSubmissionReadScope Scope, long Generation);
}
