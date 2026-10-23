using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Infrastructure.Configuration;

namespace Moodle.Infrastructure;

internal sealed class MoodleSnapshotStore(
    ConnectorDbContext dbContext,
    IMemoryCache memoryCache,
    MoodleSnapshotMetrics metrics,
    ILogger<MoodleSnapshotStore> logger,
    IOptions<MoodleSnapshotOptions>? snapshotOptions = null,
    IMoodleCredentialsProvider? credentialsProvider = null) : IMoodleSnapshotStore
{
    private static readonly TimeSpan HotTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan WarmTtl = TimeSpan.FromHours(2);
    private static readonly TimeSpan L1Duration = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, CacheVersionState> CacheVersions = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly MoodleSnapshotOptions options = (snapshotOptions?.Value ?? new MoodleSnapshotOptions()).Normalize();
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> connectionIdentityResolutions = new(StringComparer.Ordinal);

    public async Task<MoodleSnapshotEnvelope<T>?> GetReportSnapshotAsync<T>(Guid ownerId, string connectionId,
        string dataset, string courseId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || connectionId.StartsWith("legacy-", StringComparison.Ordinal))
            return null;
        var entity = await dbContext.MoodleSnapshots.AsNoTracking().SingleOrDefaultAsync(s =>
            s.OwnerId == ownerId && s.ConnectionId == connectionId && s.SnapshotType == dataset && s.CourseId == courseId,
            cancellationToken);
        return entity is null ? null : DeserializeEnvelope<T>(entity, dataset);
    }

    public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(Guid ownerId, string connectionAlias, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<CourseSummary>>(ownerId, connectionAlias, "courses", string.Empty, cancellationToken);

    public Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
        ReadAsync<CourseContentsSummary>(ownerId, connectionAlias, "activities", courseId, cancellationToken);

    public Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
        ReadAsync<CourseParticipantsPage>(ownerId, connectionAlias, "students", courseId, cancellationToken);

    public Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(Guid ownerId, string connectionAlias, string courseId, CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<CourseGroupSummary>>(ownerId, connectionAlias, "groups", courseId, cancellationToken);

    public Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(Guid ownerId, string connectionAlias, string dataset, string courseId = "", CancellationToken cancellationToken = default) =>
        ReadAsync<T>(ownerId, connectionAlias, dataset, courseId, cancellationToken);

    public async Task<IReadOnlyDictionary<string, MoodleSnapshotEnvelope<T>>> GetManyAsync<T>(
        Guid ownerId,
        string connectionAlias,
        string dataset,
        IReadOnlyCollection<string> courseIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedDataset = Normalize(dataset);
        var normalizedCourseIds = courseIds
            .Where(courseId => !string.IsNullOrWhiteSpace(courseId))
            .Select(courseId => courseId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = new Dictionary<string, MoodleSnapshotEnvelope<T>>(StringComparer.Ordinal);
        if (normalizedCourseIds.Length == 0)
        {
            return result;
        }

        var connectionId = await ResolveConnectionIdAsync(ownerId, connectionAlias, normalizedDataset, cancellationToken);
        var uncachedCourseIds = new List<string>(normalizedCourseIds.Length);
        var readLeases = new Dictionary<string, CacheVersionLease>(StringComparer.Ordinal);
        foreach (var courseId in normalizedCourseIds)
        {
            var versionKey = VersionKey(ownerId, connectionAlias, normalizedDataset, courseId);
            var readLease = AcquireCacheVersion(versionKey);
            var cacheKey = CacheKey(ownerId, connectionId, normalizedDataset, courseId, readLease.State);
            if (memoryCache.TryGetValue(cacheKey, out MoodleSnapshotEnvelope<T>? cached) && cached is not null)
            {
                metrics.RecordL1Hit(normalizedDataset);
                result[courseId] = cached;
                readLease.Dispose();
                continue;
            }

            uncachedCourseIds.Add(courseId);
            readLeases.Add(courseId, readLease);
        }

        try
        {
            var entities = new Dictionary<string, MoodleSnapshotEntity>(StringComparer.Ordinal);
            var databaseReadCommands = 0L;
            var databaseReadStarted = Stopwatch.GetTimestamp();
            try
            {
                if (uncachedCourseIds.Count > 0 && !string.IsNullOrWhiteSpace(connectionId))
                {
                    databaseReadCommands++;
                    var connectionHeads = await dbContext.Set<MoodleSnapshotEntity>().AsNoTracking()
                        .Where(item => item.OwnerId == ownerId &&
                                       item.ConnectionId == connectionId &&
                                       item.SnapshotType == normalizedDataset &&
                                       uncachedCourseIds.Contains(item.CourseId))
                        .ToListAsync(cancellationToken);
                    foreach (var head in connectionHeads)
                    {
                        entities[head.CourseId] = head;
                    }
                }

                var missingCourseIds = uncachedCourseIds
                    .Where(courseId => !entities.ContainsKey(courseId))
                    .ToArray();
                if (missingCourseIds.Length > 0)
                {
                    databaseReadCommands++;
                    var legacyHeads = await dbContext.Set<MoodleSnapshotEntity>().AsNoTracking()
                        .Where(item => item.OwnerId == ownerId &&
                                       item.ConnectionId == string.Empty &&
                                       item.ConnectionAlias == connectionAlias &&
                                       item.SnapshotType == normalizedDataset &&
                                       missingCourseIds.Contains(item.CourseId))
                        .ToListAsync(cancellationToken);
                    foreach (var head in legacyHeads)
                    {
                        entities.TryAdd(head.CourseId, head);
                    }
                }
            }
            finally
            {
                if (databaseReadCommands > 0)
                {
                    metrics.RecordSnapshotRead(
                        normalizedDataset,
                        Stopwatch.GetElapsedTime(databaseReadStarted).TotalMilliseconds,
                        databaseReadCommands);
                }
            }

            foreach (var courseId in uncachedCourseIds)
            {
                if (!entities.TryGetValue(courseId, out var entity))
                {
                    metrics.RecordMiss(normalizedDataset);
                    continue;
                }

                var envelope = DeserializeEnvelope<T>(entity, normalizedDataset);
                if (envelope is null)
                {
                    metrics.RecordMiss(normalizedDataset);
                    continue;
                }

                result[courseId] = envelope;
                metrics.RecordL2Hit(normalizedDataset, entity.UpdatedAt);
                var readLease = readLeases[courseId];
                var cacheKey = CacheKey(ownerId, connectionId, normalizedDataset, courseId, readLease.State);
                CacheEnvelope(VersionKey(ownerId, connectionAlias, normalizedDataset, courseId), readLease.State, cacheKey, envelope);
            }

            return result;
        }
        finally
        {
            foreach (var lease in readLeases.Values)
            {
                lease.Dispose();
            }
        }
    }

    public async Task SaveAsync<T>(
        Guid ownerId,
        string connectionAlias,
        string dataset,
        string courseId,
        T payload,
        string tier,
        bool frozen,
        bool complete,
        int recordCount,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? snapshotRunId)
    {
        var normalizedDataset = Normalize(dataset);
        var normalizedCourseId = courseId?.Trim() ?? string.Empty;
        var connectionId = await ResolveConnectionIdAsync(ownerId, connectionAlias, normalizedDataset, cancellationToken);
        var entity = await dbContext.MoodleSnapshots.SingleOrDefaultAsync(
            item => item.OwnerId == ownerId && item.ConnectionId == connectionId && item.SnapshotType == normalizedDataset && item.CourseId == normalizedCourseId,
            cancellationToken);
        entity ??= await dbContext.MoodleSnapshots.SingleOrDefaultAsync(
            item => item.OwnerId == ownerId && item.ConnectionId == string.Empty && item.ConnectionAlias == connectionAlias && item.SnapshotType == normalizedDataset && item.CourseId == normalizedCourseId,
            cancellationToken);
        var freshUntil = now.Add(GetFreshTtl(normalizedDataset, tier, frozen));
        var staleUntil = freshUntil.Add(GetStaleWindow(normalizedDataset, frozen));
        var serialized = MoodleJsonbSerializer.Serialize(payload, JsonOptions);
        var payloadJson = serialized.Json;
        if (serialized.SanitizedCharacters > 0)
        {
            logger.LogWarning(
                "Caracteres incompatíveis com PostgreSQL jsonb removidos do snapshot. Dataset={Dataset} Count={Count}",
                normalizedDataset,
                serialized.SanitizedCharacters);
        }
        var payloadSize = Encoding.UTF8.GetByteCount(payloadJson);
        metrics.RecordPayloadBytes(normalizedDataset, payloadSize);
        var payloadLimit = options.GetPayloadLimit(normalizedDataset);
        if (payloadSize > payloadLimit)
        {
            throw new InvalidOperationException(
                $"O payload do snapshot excede o limite configurado de {payloadLimit} bytes.");
        }
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant();

        if (entity is null)
        {
            entity = new MoodleSnapshotEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                ConnectionId = connectionId,
                ConnectionAlias = connectionAlias,
                SnapshotType = normalizedDataset,
                CourseId = normalizedCourseId,
            };
            dbContext.MoodleSnapshots.Add(entity);
        }

        entity.ConnectionId = connectionId;
        entity.ConnectionAlias = connectionAlias;
        ApplySnapshot(entity, connectionId, connectionAlias, payloadJson, payloadHash, tier, frozen, complete, recordCount, now, freshUntil, staleUntil, snapshotRunId);
        const string savepointName = "moodle_snapshot_upsert";
        var transaction = dbContext.Database.CurrentTransaction;
        if (transaction is not null)
        {
            await transaction.CreateSavepointAsync(savepointName, cancellationToken);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception) && entity.Id != Guid.Empty)
        {
            if (transaction is not null)
            {
                await transaction.RollbackToSavepointAsync(savepointName, CancellationToken.None);
            }
            // Another worker may have inserted the same head between our read
            // and insert. Detach the failed insert and converge on its head.
            dbContext.Entry(entity).State = EntityState.Detached;
            entity = await dbContext.MoodleSnapshots.SingleOrDefaultAsync(
                item => item.OwnerId == ownerId && item.ConnectionId == connectionId && item.SnapshotType == normalizedDataset && item.CourseId == normalizedCourseId,
                cancellationToken)
                ?? throw new InvalidOperationException("O head do snapshot desapareceu durante o upsert concorrente.");
            ApplySnapshot(entity, connectionId, connectionAlias, payloadJson, payloadHash, tier, frozen, complete, recordCount, now, freshUntil, staleUntil, snapshotRunId);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        Invalidate(ownerId, connectionAlias, normalizedDataset, normalizedCourseId);
        metrics.RecordRefresh(normalizedDataset);
    }

    public Task SaveAsync<T>(
        Guid ownerId,
        string connectionAlias,
        string dataset,
        string courseId,
        T payload,
        string tier,
        bool frozen,
        bool complete,
        int recordCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        SaveAsync(ownerId, connectionAlias, dataset, courseId, payload, tier, frozen, complete, recordCount, now, cancellationToken, null);

    public void Invalidate(Guid ownerId, string connectionAlias, string dataset, string courseId = "")
    {
        var normalizedAlias = connectionAlias.Trim().ToLowerInvariant();
        var normalizedDataset = Normalize(dataset);
        var normalizedCourseId = courseId?.Trim() ?? string.Empty;
        var versionKey = VersionKey(ownerId, normalizedAlias, normalizedDataset, normalizedCourseId);
        if (TryRemoveCacheVersion(versionKey, out var state) && state is not null)
        {
            state.Cancel();
        }
    }

    private async Task<MoodleSnapshotEnvelope<T>?> ReadAsync<T>(Guid ownerId, string connectionAlias, string type, string courseId, CancellationToken cancellationToken)
    {
        type = Normalize(type);
        courseId = courseId?.Trim() ?? string.Empty;
        var connectionId = await ResolveConnectionIdAsync(ownerId, connectionAlias, type, cancellationToken);
        var versionKey = VersionKey(ownerId, connectionAlias, type, courseId);
        using var readerLease = AcquireCacheVersion(versionKey);
        var state = readerLease.State;
        var key = CacheKey(ownerId, connectionId, type, courseId, state);
        if (memoryCache.TryGetValue(key, out MoodleSnapshotEnvelope<T>? cached))
        {
            metrics.RecordL1Hit(type);
            return cached;
        }

        MoodleSnapshotEntity? entity;
        var databaseReadCommands = 0L;
        var databaseReadStarted = Stopwatch.GetTimestamp();
        try
        {
            databaseReadCommands++;
            entity = await dbContext.Set<MoodleSnapshotEntity>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.OwnerId == ownerId && item.ConnectionId == connectionId && item.SnapshotType == type && item.CourseId == courseId, cancellationToken);
            if (entity is null)
            {
                databaseReadCommands++;
                entity = await dbContext.Set<MoodleSnapshotEntity>().AsNoTracking()
                    .SingleOrDefaultAsync(item => item.OwnerId == ownerId && item.ConnectionId == string.Empty && item.ConnectionAlias == connectionAlias && item.SnapshotType == type && item.CourseId == courseId, cancellationToken);
            }
        }
        finally
        {
            metrics.RecordSnapshotRead(type, Stopwatch.GetElapsedTime(databaseReadStarted).TotalMilliseconds, databaseReadCommands);
        }

        if (entity is null)
        {
            metrics.RecordMiss(type);
            return null;
        }

        var envelope = DeserializeEnvelope<T>(entity, type);
        if (envelope is null)
        {
            metrics.RecordMiss(type);
            return null;
        }

        CacheEnvelope(versionKey, state, key, envelope);
        metrics.RecordL2Hit(type, entity.UpdatedAt);
        return envelope;
    }

    private static string CacheKey(Guid ownerId, string connectionId, string dataset, string courseId, CacheVersionState state) =>
        $"moodle-snapshot:{ownerId}:{connectionId}:{dataset}:{courseId}:v{state.Id}";

    private MoodleSnapshotEnvelope<T>? DeserializeEnvelope<T>(MoodleSnapshotEntity entity, string type)
    {
        try
        {
            var data = JsonSerializer.Deserialize<T>(entity.PayloadJson, JsonOptions);
            if (data is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var legacyFrozenPostCourseDataset = type is MoodleSnapshotDatasets.Gradebook or MoodleSnapshotDatasets.Submissions &&
                entity.IsFrozen &&
                !entity.FrozenAt.HasValue;
            var isFrozen = entity.IsFrozen && !legacyFrozenPostCourseDataset;
            var freshUntil = legacyFrozenPostCourseDataset
                ? entity.UpdatedAt.Add(GetFreshTtl(type, entity.Tier, frozen: false))
                : entity.FreshUntil ?? entity.UpdatedAt.Add(
                    entity.Tier.Equals("hot", StringComparison.OrdinalIgnoreCase) ? HotTtl : WarmTtl);
            var staleUntil = legacyFrozenPostCourseDataset
                ? freshUntil.Add(GetStaleWindow(type, frozen: false))
                : entity.StaleUntil ?? freshUntil.Add(GetStaleWindow(type, isFrozen));
            if (!isFrozen && now > staleUntil)
            {
                return null;
            }

            return new MoodleSnapshotEnvelope<T>(
                data,
                entity.UpdatedAt,
                !isFrozen && now >= freshUntil,
                isFrozen,
                entity.Tier,
                freshUntil,
                staleUntil,
                entity.LastAttemptAt,
                entity.LastError,
                entity.IsComplete,
                entity.RecordCount,
                entity.LastRunId);
        }
        catch (JsonException)
        {
            logger.LogWarning("Snapshot JSON inválido. Dataset={Dataset}", type);
            return null;
        }
    }

    private void CacheEnvelope<T>(
        string versionKey,
        CacheVersionState state,
        string key,
        MoodleSnapshotEnvelope<T> envelope)
    {
        var cacheOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = L1Duration,
        };
        cacheOptions.AddExpirationToken(new CancellationChangeToken(state.Token));
        // Each physical entry owns a reference, including replacements. Its
        // delayed callback must not release a newer entry or an active reader.
        var entryLease = new CacheVersionLease(versionKey, state);
        cacheOptions.RegisterPostEvictionCallback(
            static (_, _, _, callbackState) => ((CacheVersionLease)callbackState!).Dispose(),
            entryLease);
        try
        {
            memoryCache.Set(key, envelope, cacheOptions);
        }
        catch
        {
            entryLease.Dispose();
            throw;
        }
    }

    private static bool TryRemoveCacheVersion(string versionKey, out CacheVersionState? state)
    {
        if (CacheVersions.TryGetValue(versionKey, out state) &&
            ((ICollection<KeyValuePair<string, CacheVersionState>>)CacheVersions).Remove(
                new KeyValuePair<string, CacheVersionState>(versionKey, state)))
        {
            return true;
        }

        state = null;
        return false;
    }

    private static CacheVersionLease AcquireCacheVersion(string versionKey)
    {
        while (true)
        {
            var state = CacheVersions.GetOrAdd(versionKey, static _ => new CacheVersionState());
            lock (state)
            {
                // A last release may have retired this generation while we
                // waited for its lock. Never resurrect a detached generation.
                if (CacheVersions.TryGetValue(versionKey, out var current) && ReferenceEquals(current, state))
                {
                    return new CacheVersionLease(versionKey, state);
                }
            }
        }
    }

    private static string VersionKey(Guid ownerId, string connectionAlias, string dataset, string courseId) =>
        $"{ownerId:N}:{connectionAlias.Trim().ToLowerInvariant()}:{dataset}:{courseId}";

    private async Task<string> ResolveConnectionIdAsync(
        Guid ownerId,
        string connectionAlias,
        string dataset,
        CancellationToken cancellationToken)
    {
        var identityStartedAt = Stopwatch.GetTimestamp();
        var requestedAlias = MoodleConnectionAlias.Normalize(connectionAlias);
        var resolvedCredentials = credentialsProvider?.TryGetResolvedCredentials(connectionAlias);
        if (resolvedCredentials is not null &&
            requestedAlias is not null &&
            !string.IsNullOrWhiteSpace(resolvedCredentials.ConnectionId) &&
            string.Equals(
                MoodleConnectionAlias.Normalize(resolvedCredentials.Alias),
                requestedAlias,
                StringComparison.Ordinal))
        {
            metrics.RecordIdentityResolution(
                dataset,
                Stopwatch.GetElapsedTime(identityStartedAt).TotalMilliseconds,
                0);
            return resolvedCredentials.ConnectionId;
        }

        var key = $"{ownerId:N}:{connectionAlias.Trim().ToLowerInvariant()}";
        var databaseCommands = 0L;
        var resolutionCandidate = new Lazy<Task<string>>(
            () => MoodleConnectionIdentity.ResolveAsync(
                dbContext,
                ownerId,
                string.Empty,
                connectionAlias,
                cancellationToken,
                () => databaseCommands++),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var resolution = connectionIdentityResolutions.GetOrAdd(key, resolutionCandidate);
        var ownsResolution = ReferenceEquals(resolution, resolutionCandidate);
        var startedAt = identityStartedAt;

        try
        {
            var connectionId = await resolution.Value;
            metrics.RecordIdentityResolution(
                dataset,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                ownsResolution ? databaseCommands : 0);
            return connectionId;
        }
        catch
        {
            metrics.RecordIdentityResolution(
                dataset,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                ownsResolution ? databaseCommands : 0);
            ((ICollection<KeyValuePair<string, Lazy<Task<string>>>>)connectionIdentityResolutions).Remove(
                new KeyValuePair<string, Lazy<Task<string>>>(key, resolution));
            throw;
        }
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private sealed class CacheVersionState
    {
        public int References { get; set; }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token => Cancellation.Token;

        public void Cancel()
        {
            if (!Cancellation.IsCancellationRequested)
            {
                Cancellation.Cancel();
            }
        }
    }

    private sealed class CacheVersionLease : IDisposable
    {
        private readonly string versionKey;
        private CacheVersionState? ownedState;
        public CacheVersionState State { get; }

        public CacheVersionLease(string versionKey, CacheVersionState state)
        {
            this.versionKey = versionKey;
            State = state;
            ownedState = state;
            lock (state) { state.References++; }
        }

        public void Dispose()
        {
            var state = Interlocked.Exchange(ref ownedState, null);
            if (state is null) return;
            lock (state)
            {
                if (--state.References == 0)
                {
                    ((ICollection<KeyValuePair<string, CacheVersionState>>)CacheVersions).Remove(
                        new KeyValuePair<string, CacheVersionState>(versionKey, state));
                }
            }
        }
    }

    private static void ApplySnapshot(
        MoodleSnapshotEntity entity,
        string connectionId,
        string connectionAlias,
        string payloadJson,
        string payloadHash,
        string tier,
        bool frozen,
        bool complete,
        int recordCount,
        DateTimeOffset now,
        DateTimeOffset freshUntil,
        DateTimeOffset staleUntil,
        Guid? snapshotRunId)
    {
        entity.ConnectionId = connectionId;
        entity.ConnectionAlias = connectionAlias;
        if (!string.Equals(entity.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
        {
            entity.PayloadJson = payloadJson;
        }
        entity.Tier = tier;
        entity.IsFrozen = frozen;
        entity.FrozenAt = frozen ? entity.FrozenAt ?? now : null;
        entity.UpdatedAt = now;
        entity.FreshUntil = freshUntil;
        entity.StaleUntil = staleUntil;
        entity.LastAttemptAt = now;
        entity.LastError = null;
        entity.PayloadHash = payloadHash;
        entity.IsComplete = complete;
        entity.RecordCount = recordCount;
        if (snapshotRunId is not null)
        {
            entity.LastRunId = snapshotRunId;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation;

    private TimeSpan GetFreshTtl(string dataset, string tier, bool frozen) =>
        frozen ? TimeSpan.FromDays(3650) : dataset switch
        {
            MoodleSnapshotDatasets.Courses => TimeSpan.FromDays(2),
            MoodleSnapshotDatasets.Activities => TimeSpan.FromHours(24),
            MoodleSnapshotDatasets.Students or MoodleSnapshotDatasets.Groups => tier.Equals("hot", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(1) : TimeSpan.FromHours(4),
            MoodleSnapshotDatasets.Submissions => TimeSpan.FromMinutes(15),
            MoodleSnapshotDatasets.Gradebook => TimeSpan.FromMinutes(options.GradebookFreshMinutes),
            MoodleSnapshotDatasets.DashboardPending => TimeSpan.FromHours(24),
            MoodleSnapshotDatasets.DashboardAccess => TimeSpan.FromMinutes(45),
            _ => tier.Equals("hot", StringComparison.OrdinalIgnoreCase) ? HotTtl : WarmTtl,
        };

    private TimeSpan GetStaleWindow(string dataset, bool frozen) =>
        frozen ? TimeSpan.FromDays(3650) : dataset switch
        {
            MoodleSnapshotDatasets.Courses => TimeSpan.FromDays(7),
            MoodleSnapshotDatasets.Activities => TimeSpan.FromDays(3),
            MoodleSnapshotDatasets.Students or MoodleSnapshotDatasets.Groups => TimeSpan.FromHours(24),
            MoodleSnapshotDatasets.Submissions => TimeSpan.FromHours(6),
            MoodleSnapshotDatasets.Gradebook => TimeSpan.FromMinutes(options.GradebookStaleMinutes),
            MoodleSnapshotDatasets.DashboardPending => TimeSpan.FromDays(3),
            MoodleSnapshotDatasets.DashboardAccess => TimeSpan.FromHours(6),
            _ => TimeSpan.FromHours(12),
        };
}
