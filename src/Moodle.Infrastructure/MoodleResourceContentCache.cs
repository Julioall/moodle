using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure;

internal enum MoodleResourceContentSource
{
    CacheHit,
    Downloaded,
    SharedDownload
}

internal sealed record MoodleResourceContent(
    byte[] Content,
    string MimeType,
    long SizeBytes,
    string Sha256Hex,
    MoodleResourceContentSource Source,
    double? DownloadDurationMs = null);

/// <summary>
/// Bounded, process-local cache for verified Moodle resource bytes. Authorization
/// stays in MoodleResourceGateway and must succeed before this cache is queried.
/// </summary>
internal sealed class MoodleResourceContentCache : IDisposable
{
    internal const long DefaultSizeLimitBytes = 128L * 1024 * 1024;
    internal const long DefaultMaxEntryBytes = 25L * 1024 * 1024;
    internal static readonly TimeSpan MaximumTtl = TimeSpan.FromMinutes(15);

    private readonly object sync = new();
    private readonly MemoryCache cache;
    private readonly TimeProvider timeProvider;
    private readonly long sizeLimitBytes;
    private readonly long maxEntryBytes;
    private readonly Dictionary<ContentKey, EntryMetadata> entries = [];
    private readonly LinkedList<ContentKey> lru = [];
    private readonly ConcurrentDictionary<string, DownloadFlight> flights = new(StringComparer.Ordinal);
    private long currentSizeBytes;

    public MoodleResourceContentCache(
        TimeProvider timeProvider,
        long sizeLimitBytes = DefaultSizeLimitBytes,
        long maxEntryBytes = DefaultMaxEntryBytes)
    {
        if (sizeLimitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeLimitBytes));
        }

        if (maxEntryBytes <= 0 || maxEntryBytes > sizeLimitBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntryBytes));
        }

        this.timeProvider = timeProvider;
        this.sizeLimitBytes = sizeLimitBytes;
        this.maxEntryBytes = maxEntryBytes;
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = sizeLimitBytes });
    }

    internal long CurrentSizeBytes
    {
        get
        {
            lock (sync)
            {
                PruneExpiredEntries(timeProvider.GetUtcNow());
                return currentSizeBytes;
            }
        }
    }

    internal int EntryCountForTesting
    {
        get
        {
            lock (sync)
            {
                PruneExpiredEntries(timeProvider.GetUtcNow());
                return entries.Count;
            }
        }
    }

    internal int ActiveFlightWaiterCountForTesting(string resourceId) =>
        flights.TryGetValue(resourceId, out var flight) ? flight.WaiterCount : 0;

    public async Task<MoodleResourceContent> GetOrDownloadAsync(
        string clientId,
        string connectionId,
        string resourceId,
        string? expectedSha256,
        DateTimeOffset resourceExpiresAt,
        long maxBytes,
        Func<CancellationToken, Task<SubmissionFileDownloadResult>> downloader,
        CancellationToken cancellationToken,
        Func<MoodleResourceContent, CancellationToken, Task>? persistVerifiedContent = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        if (resourceExpiresAt <= now)
        {
            throw new MoodleResourceException("RESOURCE_EXPIRED", "O resource Moodle expirou.");
        }

        if (TryGetCached(clientId, connectionId, expectedSha256, resourceExpiresAt, out var cached))
        {
            VerifyForCaller(cached, expectedSha256, maxBytes);
            return cached;
        }

        while (true)
        {
            var candidate = new DownloadFlight();
            var flight = flights.GetOrAdd(resourceId, candidate);
            var isOwner = ReferenceEquals(candidate, flight);
            if (!isOwner)
            {
                candidate.Dispose();
            }

            if (!flight.TryAddWaiter())
            {
                RemoveFlight(resourceId, flight);
                if (isOwner)
                {
                    flight.Dispose();
                }

                cancellationToken.ThrowIfCancellationRequested();

                continue;
            }

            var resolvedByCache = false;
            if (isOwner)
            {
                // Close the small race where another resource with the same
                // content hash filled the cache after our first lookup.
                if (TryGetCached(clientId, connectionId, expectedSha256, resourceExpiresAt, out cached))
                {
                    resolvedByCache = true;
                    flight.Complete(new FlightResult(cached, null));
                    RemoveFlight(resourceId, flight);
                }
                else
                {
                    _ = CompleteDownloadAsync(
                        resourceId,
                        flight,
                        clientId,
                        connectionId,
                        expectedSha256,
                        resourceExpiresAt,
                        maxBytes,
                        downloader,
                        persistVerifiedContent);
                }
            }

            try
            {
                var result = await flight.Task.WaitAsync(cancellationToken);
                if (result.Error is not null)
                {
                    ExceptionDispatchInfo.Capture(result.Error).Throw();
                }

                var content = result.Content
                    ?? throw new InvalidOperationException("A conclução do download Moodle nao continha conteudo.");
                VerifyForCaller(content, expectedSha256, maxBytes);
                var source = resolvedByCache
                    ? MoodleResourceContentSource.CacheHit
                    : isOwner
                        ? MoodleResourceContentSource.Downloaded
                        : MoodleResourceContentSource.SharedDownload;
                var bytes = isOwner || resolvedByCache ? content.Content : (byte[])content.Content.Clone();
                return content with { Content = bytes, Source = source };
            }
            finally
            {
                flight.RemoveWaiter();
            }
        }
    }

    public void Dispose() => cache.Dispose();

    private async Task CompleteDownloadAsync(
        string resourceId,
        DownloadFlight flight,
        string clientId,
        string connectionId,
        string? expectedSha256,
        DateTimeOffset resourceExpiresAt,
        long maxBytes,
        Func<CancellationToken, Task<SubmissionFileDownloadResult>> downloader,
        Func<MoodleResourceContent, CancellationToken, Task>? persistVerifiedContent)
    {
        try
        {
            flight.CancellationToken.ThrowIfCancellationRequested();
            var startedAt = timeProvider.GetTimestamp();
            var download = await downloader(flight.CancellationToken);
            var downloadDurationMs = timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            flight.CancellationToken.ThrowIfCancellationRequested();
            var verified = VerifyDownload(download, expectedSha256, maxBytes);
            if (persistVerifiedContent is not null)
            {
                await persistVerifiedContent(verified, flight.CancellationToken);
            }

            flight.CancellationToken.ThrowIfCancellationRequested();
            Store(clientId, connectionId, resourceExpiresAt, verified);
            flight.Complete(new FlightResult(verified with { DownloadDurationMs = downloadDurationMs }, null));
        }
        catch (Exception exception)
        {
            flight.Complete(new FlightResult(null, exception));
        }
        finally
        {
            RemoveFlight(resourceId, flight);
            flight.Dispose();
        }
    }

    private static MoodleResourceContent VerifyDownload(
        SubmissionFileDownloadResult download,
        string? expectedSha256,
        long maxBytes)
    {
        if (download.Truncated || download.SizeBytes > maxBytes || download.Content.LongLength > maxBytes)
        {
            throw new MoodleResourceException("RESOURCE_TOO_LARGE", "O arquivo excede o limite configurado para resources.");
        }

        if (download.SizeBytes < 0 || download.SizeBytes != download.Content.LongLength)
        {
            throw new MoodleResourceException("RESOURCE_HASH_MISMATCH", "O arquivo Moodle retornou tamanho divergente.");
        }

        var actualSha256 = Convert.ToHexString(SHA256.HashData(download.Content)).ToLowerInvariant();
        if (!string.Equals(actualSha256, download.Sha256Hex, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(expectedSha256) &&
             !string.Equals(expectedSha256.Trim(), actualSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MoodleResourceException("RESOURCE_HASH_MISMATCH", "O arquivo Moodle foi alterado desde o registro do resource.");
        }

        return new MoodleResourceContent(
            download.Content,
            string.IsNullOrWhiteSpace(download.MimeType) ? "application/octet-stream" : download.MimeType,
            download.Content.LongLength,
            actualSha256,
            MoodleResourceContentSource.Downloaded);
    }

    private static void VerifyForCaller(MoodleResourceContent content, string? expectedSha256, long maxBytes)
    {
        if (content.SizeBytes > maxBytes || content.Content.LongLength > maxBytes)
        {
            throw new MoodleResourceException("RESOURCE_TOO_LARGE", "O arquivo excede o limite configurado para resources.");
        }

        if (content.SizeBytes != content.Content.LongLength ||
            !string.Equals(content.Sha256Hex, Convert.ToHexString(SHA256.HashData(content.Content)).ToLowerInvariant(), StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(expectedSha256) &&
             !string.Equals(expectedSha256.Trim(), content.Sha256Hex, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MoodleResourceException("RESOURCE_HASH_MISMATCH", "O arquivo Moodle foi alterado desde o registro do resource.");
        }
    }

    private bool TryGetCached(
        string clientId,
        string connectionId,
        string? expectedSha256,
        DateTimeOffset resourceExpiresAt,
        out MoodleResourceContent content)
    {
        content = null!;
        if (!IsSha256(expectedSha256))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (resourceExpiresAt <= now)
        {
            return false;
        }

        var key = new ContentKey(clientId, connectionId, expectedSha256!.Trim().ToLowerInvariant());
        lock (sync)
        {
            PruneExpiredEntries(now);
            if (!cache.TryGetValue(key, out MoodleResourceContent? cached) || cached is null)
            {
                RemoveMetadata(key);
                return false;
            }

            if (entries.TryGetValue(key, out var metadata))
            {
                lru.Remove(metadata.Node);
                metadata.Node = lru.AddLast(key);
            }

            var copy = (byte[])cached.Content.Clone();
            content = cached with { Content = copy, Source = MoodleResourceContentSource.CacheHit, DownloadDurationMs = null };
            return true;
        }
    }

    private void Store(
        string clientId,
        string connectionId,
        DateTimeOffset resourceExpiresAt,
        MoodleResourceContent content)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAt = resourceExpiresAt < now.Add(MaximumTtl) ? resourceExpiresAt : now.Add(MaximumTtl);
        var size = content.Content.LongLength;
        if (expiresAt <= now || size <= 0 || size > maxEntryBytes || size > sizeLimitBytes)
        {
            return;
        }

        var key = new ContentKey(clientId, connectionId, content.Sha256Hex);
        var stored = content with { Content = (byte[])content.Content.Clone(), Source = MoodleResourceContentSource.CacheHit };
        lock (sync)
        {
            PruneExpiredEntries(now);
            RemoveEntry(key);
            while (currentSizeBytes + size > sizeLimitBytes && lru.First is not null)
            {
                RemoveEntry(lru.First.Value);
            }

            var node = lru.AddLast(key);
            var metadata = new EntryMetadata(size, expiresAt, node);
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expiresAt,
                Size = size
            };
            options.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
            {
                EvictionCallback = OnEvicted,
                State = key
            });
            cache.Set(key, stored, options);
            entries.Add(key, metadata);
            currentSizeBytes += size;
        }
    }

    private void PruneExpiredEntries(DateTimeOffset now)
    {
        foreach (var key in entries
                     .Where(entry => entry.Value.ExpiresAt <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            RemoveEntry(key);
        }
    }

    private void RemoveEntry(ContentKey key)
    {
        if (!entries.Remove(key, out var metadata))
        {
            cache.Remove(key);
            return;
        }

        lru.Remove(metadata.Node);
        currentSizeBytes -= metadata.SizeBytes;
        cache.Remove(key);
    }

    private void RemoveMetadata(ContentKey key)
    {
        if (entries.Remove(key, out var metadata))
        {
            lru.Remove(metadata.Node);
            currentSizeBytes -= metadata.SizeBytes;
        }
    }

    private void OnEvicted(object key, object? value, EvictionReason reason, object? state)
    {
        if (state is not ContentKey contentKey)
        {
            return;
        }

        lock (sync)
        {
            RemoveMetadata(contentKey);
        }
    }

    private void RemoveFlight(string resourceId, DownloadFlight flight) =>
        ((ICollection<KeyValuePair<string, DownloadFlight>>)flights)
            .Remove(new KeyValuePair<string, DownloadFlight>(resourceId, flight));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private readonly record struct ContentKey(string ClientId, string ConnectionId, string Sha256);

    private sealed class EntryMetadata(long sizeBytes, DateTimeOffset expiresAt, LinkedListNode<ContentKey> node)
    {
        public long SizeBytes { get; } = sizeBytes;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public LinkedListNode<ContentKey> Node { get; set; } = node;
    }

    private sealed record FlightResult(MoodleResourceContent? Content, Exception? Error);

    private sealed class DownloadFlight : IDisposable
    {
        private readonly object sync = new();
        private readonly CancellationTokenSource cancellation;
        private readonly TaskCompletionSource<FlightResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int waiterCount;
        private bool completed;

        public DownloadFlight() => cancellation = new CancellationTokenSource();

        public Task<FlightResult> Task => completion.Task;
        public CancellationToken CancellationToken => cancellation.Token;
        public int WaiterCount
        {
            get
            {
                lock (sync)
                {
                    return waiterCount;
                }
            }
        }

        public bool TryAddWaiter()
        {
            lock (sync)
            {
                if (completed || cancellation.IsCancellationRequested)
                {
                    return false;
                }

                waiterCount++;
                return true;
            }
        }

        public void RemoveWaiter()
        {
            lock (sync)
            {
                waiterCount = Math.Max(0, waiterCount - 1);
                if (waiterCount == 0 && !completed)
                {
                    cancellation.Cancel();
                }
            }
        }

        public void Complete(FlightResult result)
        {
            lock (sync)
            {
                if (completed)
                {
                    return;
                }

                completed = true;
                completion.TrySetResult(result);
            }
        }

        public void Dispose() => cancellation.Dispose();
    }
}
