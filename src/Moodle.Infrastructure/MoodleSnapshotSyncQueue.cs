using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions;
using Moodle.Domain;
using Moodle.Infrastructure.Configuration;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Infrastructure;

internal sealed class MoodleSnapshotSyncQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<MoodleSnapshotSyncQueue> logger,
    MoodleSnapshotMetrics metrics,
    IOptions<MoodleSnapshotOptions> snapshotOptions,
    TimeSpan? courseOverviewSyncTimeout = null) : BackgroundService, IMoodleSnapshotSyncQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly string WorkerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly MoodleSnapshotOptions _options = snapshotOptions.Value.Normalize();
    private readonly TimeSpan _courseOverviewSyncTimeout = courseOverviewSyncTimeout ?? TimeSpan.FromSeconds(60);
    // Durable work stays in moodle_sync_states; this bounded scheduler is only
    // the in-process dispatcher and is rebuilt by the polling loop on restart.
    private readonly MoodleSnapshotWorkScheduler _scheduler = new(snapshotOptions.Value.Normalize(), metrics.RecordThrottleWait);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _enqueuedAtTimestamp = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly AsyncLocal<DatabaseCommandCounter?> _activeTransactionCommands = new();
    private readonly DateTimeOffset _applicationStartedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;

    public bool Enqueue(MoodleSnapshotSyncRequest request) =>
        EnqueueAsync(request).GetAwaiter().GetResult();

    public async Task<bool> EnqueueAsync(
        MoodleSnapshotSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        request = Normalize(request);
        var now = DateTimeOffset.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
        {
            var identityStartedAt = Stopwatch.GetTimestamp();
            long identityCommands = 0;
            try
            {
                request = request with
                {
                    ConnectionId = await MoodleConnectionIdentity.ResolveAsync(
                        db,
                        request.OwnerId,
                        request.ClientId,
                        request.ConnectionAlias,
                        cancellationToken,
                        () => identityCommands++)
                };
            }
            finally
            {
                metrics.RecordIdentityResolution(
                    request.Dataset,
                    Stopwatch.GetElapsedTime(identityStartedAt).TotalMilliseconds,
                    identityCommands);
            }
        }
        else
        {
            request = request with { ConnectionId = request.ConnectionId.Trim() };
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            now = DateTimeOffset.UtcNow;
            // The alias is the durable scope key, while the connection id keeps
            // renamed aliases attached to the same durable state.
            var state = await db.MoodleSyncStates.SingleOrDefaultAsync(
                item => item.OwnerId == request.OwnerId &&
                        (item.ConnectionId == request.ConnectionId ||
                         item.ConnectionAlias == request.ConnectionAlias) &&
                        item.Dataset == request.Dataset &&
                        item.CourseId == (request.CourseId ?? string.Empty),
                cancellationToken);
            // The database read can wait behind another caller's insert. Compare
            // scheduling timestamps against the time after that read so a newly
            // committed due row is not mistaken for future work.
            now = DateTimeOffset.UtcNow;

            if (state is not null && MoodleSyncLeasePolicy.IsActive(state, now))
            {
                if (!string.Equals(state.WorkPolicy, request.WorkPolicy, StringComparison.Ordinal))
                {
                    return false;
                }
                // A running job already owns the key. A subsequent force refresh is
                // represented by the next scheduled attempt instead of duplicating
                // the expensive Moodle fan-out.
                if (request.Force && !state.ForceRequested)
                {
                    state.ForceRequested = true;
                    state.NextSyncAt = now;
                    state.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                }

                return true;
            }

            if (state is null)
            {
                state = new MoodleSyncStateEntity
                {
                    Id = Guid.NewGuid(),
                    OwnerId = request.OwnerId,
                    ConnectionId = request.ConnectionId!,
                    ConnectionAlias = request.ConnectionAlias,
                    Dataset = request.Dataset,
                    WorkPolicy = request.WorkPolicy,
                    CourseId = request.CourseId ?? string.Empty,
                };
                db.MoodleSyncStates.Add(state);
            }
            else if (!request.Force &&
                     string.Equals(state.Status, "pending", StringComparison.OrdinalIgnoreCase) &&
                     state.NextSyncAt is { } scheduled &&
                     scheduled <= now)
            {
                if (!string.Equals(state.WorkPolicy, request.WorkPolicy, StringComparison.Ordinal)) return false;
                // The durable work is already due. Avoid rewriting the same row on
                // every dashboard poll; the in-memory signal remains enough.
                return TryEnqueueSignal(request) || IsSignalPending(request);
            }
            else if (!request.Force && state.NextSyncAt is { } next && next > now)
            {
                return false;
            }

            state.ConnectionId = request.ConnectionId!;
            state.ConnectionAlias = request.ConnectionAlias;
            state.WorkPolicy = request.WorkPolicy;
            state.ClientId = request.ClientId;
            state.UserExternalId = request.UserExternalId;
            state.Priority = Math.Clamp(request.Priority, 0, 1000);
            state.Status = "pending";
            state.NextSyncAt = now;
            state.LastError = null;
            state.ForceRequested |= request.Force;
            state.UpdatedAt = now;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsSyncStateUniqueViolation(exception) && attempt == 0)
            {
                // Two requests can observe an empty scope before either insert
                // commits. Detach the failed insert and converge on the row that
                // won the unique scope constraint, then re-evaluate its lease.
                db.ChangeTracker.Clear();
                continue;
            }

            // Another caller may signal the committed row before its creator
            // reaches the scheduler. An already accepted signal is success too.
            return TryEnqueueSignal(request) || IsSignalPending(request);
        }

        throw new InvalidOperationException("Não foi possível enfileirar a sincronização Moodle após uma corrida de escopo.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MoodleSnapshotSyncQueue iniciada.");
        var workers = Enumerable
            .Range(0, _options.GlobalConcurrency)
            .Select(_ => ConsumeQueueAsync(stoppingToken))
            .ToArray();

        try
        {
            await RemoveLegacyEagerPrefetchStatesAsync(stoppingToken);
            await RecoverOrphanedStatesAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await EnqueueDueStatesAsync(stoppingToken);

                if (DateTimeOffset.UtcNow - _lastCleanupAt >= TimeSpan.FromMinutes(_options.CleanupIntervalMinutes))
                {
                    _lastCleanupAt = DateTimeOffset.UtcNow;
                    await CleanupRunsAsync(stoppingToken);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        logger.LogInformation("MoodleSnapshotSyncQueue encerrada.");
    }

    private async Task ConsumeQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await _scheduler.TakeAsync(cancellationToken);
                try
                {
                    await ProcessRequestAsync(request, cancellationToken);
                }
                finally
                {
                    _scheduler.Complete(request);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RemoveLegacyEagerPrefetchStatesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();

        // Before lazy snapshots, a connection refresh scheduled four jobs for
        // every course. Clear only those precise legacy signatures, leaving
        // user-triggered jobs (which use priority 5, 10, or 20) untouched.
        var legacyStates = await db.MoodleSyncStates
            .Where(item =>
                item.CourseId != string.Empty &&
                (item.Status == "pending" || item.Status == "failed") &&
                ((item.Dataset == MoodleSnapshotDatasets.Activities && item.Priority == 20) ||
                 (item.Dataset == MoodleSnapshotDatasets.Students && item.Priority == 30) ||
                 (item.Dataset == MoodleSnapshotDatasets.Groups && item.Priority == 60) ||
                 (item.Dataset == MoodleSnapshotDatasets.Submissions && item.Priority == 15)))
            .ToListAsync(cancellationToken);

        if (legacyStates.Count > 0)
        {
            db.MoodleSyncStates.RemoveRange(legacyStates);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Removed {Count} legacy eager Moodle snapshot jobs.",
                legacyStates.Count);
        }
    }

    private async Task EnqueueDueStatesAsync(CancellationToken cancellationToken)
    {
        var requests = await LoadDueRequestsAsync(cancellationToken);
        foreach (var request in requests)
        {
            TryEnqueueSignal(request);
        }
    }

    internal async Task<IReadOnlyList<MoodleSnapshotSyncRequest>> LoadDueRequestsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var starvationCutoff = now.AddHours(-_options.QueueStarvationHours);
        // Admit a bounded number of best due jobs per connection before applying the
        // bounded batch. A flat Take(32) lets one connection's backlog
        // exclude every other connection from the in-memory scheduler.
        var states = await db.MoodleSyncStates
            .FromSqlInterpolated($"""
                SELECT due."Id", due."OwnerId", due."ConnectionId", due."ConnectionAlias", due."Dataset",
                       due."CourseId", due."Status", due."LastStartedAt", due."LastAttemptAt", due."LastCompletedAt",
                       due."NextSyncAt", due."LastError", due."RecordsSynced", due."ClientId", due."UserExternalId",
                       due."Priority", due."LeaseUntil", due."AttemptCount", due."ForceRequested", due."UpdatedAt",
                       due."WorkPolicy"
                FROM (
                    SELECT state.*, ROW_NUMBER() OVER (
                        PARTITION BY state."OwnerId", CASE
                            WHEN state."ConnectionId" <> '' THEN 'connection:' || state."ConnectionId"
                            ELSE 'alias:' || lower(state."ConnectionAlias")
                        END
                        ORDER BY CASE WHEN state."NextSyncAt" < {starvationCutoff} THEN 0 ELSE 1 END,
                                 state."ForceRequested" DESC,
                                 state."Priority",
                                 state."NextSyncAt",
                                 state."Id"
                    ) AS "RowNumber"
                    FROM moodle_sync_states AS state
                    WHERE state."ClientId" <> ''
                      AND state."NextSyncAt" IS NOT NULL
                      AND state."NextSyncAt" <= {now}
                      AND (state."Status" <> 'running' OR state."LeaseUntil" IS NULL OR state."LeaseUntil" <= {now})
                ) AS due
                WHERE due."RowNumber" <= {_options.PerConnectionConcurrency}
                ORDER BY CASE WHEN due."NextSyncAt" < {starvationCutoff} THEN 0 ELSE 1 END,
                         due."ForceRequested" DESC,
                         due."Priority",
                         due."NextSyncAt",
                         due."Id"
                LIMIT {_options.QueueCapacity}
                """)
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

        return states
            .Select(state => new MoodleSnapshotSyncRequest(
                state.OwnerId,
                state.ClientId,
                state.ConnectionAlias,
                state.UserExternalId,
                state.ForceRequested,
                state.Dataset,
                string.IsNullOrWhiteSpace(state.CourseId) ? null : state.CourseId,
                state.Priority,
                state.ConnectionId,
                "recovered",
                state.WorkPolicy))
            .ToArray();
    }

    private async Task CleanupRunsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RunRetentionDays);
            var expiredRunIds = await db.MoodleSnapshotRuns
                .Where(run => run.StartedAt < cutoff &&
                              run.Status != "running" &&
                              !db.MoodleSnapshots.Any(snapshot => snapshot.LastRunId == run.Id))
                .Select(run => run.Id)
                .ToArrayAsync(cancellationToken);
            if (expiredRunIds.Length == 0)
            {
                return;
            }

            await db.MoodleSnapshotRunItems
                .Where(item => expiredRunIds.Contains(item.RunId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.MoodleSnapshotRuns
                .Where(run => expiredRunIds.Contains(run.Id))
                .ExecuteDeleteAsync(cancellationToken);
            metrics.RecordRunsCleaned(expiredRunIds.Length);
            logger.LogInformation("Runs de snapshot expirados removidos. Count={Count} RetentionDays={RetentionDays}", expiredRunIds.Length, _options.RunRetentionDays);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning("Não foi possível limpar runs técnicos de snapshot expirados. FailureReason={FailureReason}", "database_error");
        }
    }

    private async Task RecoverOrphanedStatesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var orphanedStates = await db.MoodleSyncStates
            .Where(item => item.Dataset != MoodleSnapshotDatasets.DashboardPending &&
                           item.Status == "running" &&
                           (item.LeaseUntil == null || item.LeaseUntil <= now) &&
                           (item.LastStartedAt == null || item.LastStartedAt < _applicationStartedAt))
            .ToListAsync(cancellationToken);

        foreach (var orphanedState in orphanedStates)
        {
            orphanedState.Status = "pending";
            orphanedState.LeaseUntil = null;
            orphanedState.NextSyncAt = now;
            orphanedState.LastError = null;
            orphanedState.UpdatedAt = now;
        }

        var recovered = orphanedStates.Count;
        if (recovered > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (recovered > 0)
        {
            logger.LogWarning(
                "Estados de sincronização órfãos recuperados após reinicialização. Count={Count}",
                recovered);
        }
    }

    internal async Task ProcessRequestAsync(
        MoodleSnapshotSyncRequest request,
        CancellationToken cancellationToken)
    {
        var key = BuildKey(request);
        long enqueuedAtTimestamp = 0;
        lock (_gate)
        {
            if (_enqueuedAtTimestamp.Remove(key, out var storedTimestamp))
            {
                enqueuedAtTimestamp = storedTimestamp;
            }
        }
        if (enqueuedAtTimestamp != 0)
        {
            metrics.RecordQueueWait(request.Dataset, Stopwatch.GetElapsedTime(enqueuedAtTimestamp).TotalMilliseconds);
        }

        CancellationTokenSource? leaseHeartbeatCancellation = null;
        Task? leaseHeartbeat = null;
        Guid? runId = null;
        Guid? runItemId = null;
        SyncWork? claimedWork = null;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        long transactionStartedAt = 0;
        DatabaseCommandCounter? transactionCommandCounter = null;
        long publishStartedAt = 0;
        long publishCommandCountBefore = 0;
        var publishMetricRecorded = false;
        long syncStartedAt = 0;
        var syncMetricRecorded = false;
        var runStartedAt = DateTimeOffset.UtcNow;
        try
        {
            var claimStartedAt = Stopwatch.GetTimestamp();
            long claimCommands = 0;
            SyncWork? work;
            try
            {
                work = await TryClaimAsync(request, cancellationToken, () => claimCommands++);
            }
            finally
            {
                metrics.RecordLeaseClaim(
                    request.Dataset,
                    Stopwatch.GetElapsedTime(claimStartedAt).TotalMilliseconds,
                    claimCommands);
            }
            claimedWork = work;
            if (work is null)
            {
                return;
            }

            leaseHeartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            leaseHeartbeat = RenewLeaseAsync(work, leaseHeartbeatCancellation.Token);

            using var scope = scopeFactory.CreateScope();
            var executionContext = scope.ServiceProvider.GetRequiredService<IConnectorExecutionContext>();
            var selection = scope.ServiceProvider.GetRequiredService<IMoodleConnectionSelection>();
            executionContext.Enter(work.ClientId, work.OwnerId.ToString(), null);
            selection.Alias = work.ConnectionAlias;

            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            var run = await StartRunAsync(db, work, request.Trigger, cancellationToken);
            runId = run.Id;
            work = work with { RunId = run.Id };
            claimedWork = work;
            var item = new MoodleSnapshotRunItemEntity
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Dataset = work.Dataset,
                ResourceId = work.CourseId ?? string.Empty,
                StartedAt = DateTimeOffset.UtcNow,
            };
            runItemId = item.Id;
            db.MoodleSnapshotRunItems.Add(item);
            run.ItemsTotal = 1;
            await db.SaveChangesAsync(cancellationToken);
            // Perform upstream reads and prepare payloads before opening a
            // transaction so a slow Moodle response does not hold DB locks.
            syncStartedAt = Stopwatch.GetTimestamp();
            DateTimeOffset? admissionDeadlineUtc = IsCourseOverviewWorkPolicy(work.WorkPolicy)
                ? DateTimeOffset.UtcNow.Add(_courseOverviewSyncTimeout)
                : null;
            using var admissionWork = MoodleAdmissionWorkScope.BeginScope(
                MoodleAdmissionOrigin.Snapshot,
                work.OwnerId.ToString("N"),
                MoodleAdmissionPriority.Background,
                admissionDeadlineUtc);
            using var overviewBudget = IsCourseOverviewWorkPolicy(work.WorkPolicy)
                ? MoodleOverviewStudentSyncBudget.Begin(maximumCalls: 8, maximumDuration: _courseOverviewSyncTimeout)
                : null;
            using var overviewTimeout = IsCourseOverviewWorkPolicy(work.WorkPolicy)
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            overviewTimeout?.CancelAfter(_courseOverviewSyncTimeout);
            using var diagnosticCorrelation = MoodleCallDiagnosticsCorrelationScope.Begin(run.Id, item.Id);
            var syncResult = await SyncAsync(scope.ServiceProvider, work, overviewTimeout?.Token ?? cancellationToken);
            metrics.RecordSyncDuration(work.Dataset, Stopwatch.GetElapsedTime(syncStartedAt).TotalMilliseconds);
            syncMetricRecorded = true;

            transactionStartedAt = Stopwatch.GetTimestamp();
            transactionCommandCounter = new DatabaseCommandCounter();
            transactionCommandCounter.Increment();
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            _activeTransactionCommands.Value = transactionCommandCounter;
            publishStartedAt = Stopwatch.GetTimestamp();
            publishCommandCountBefore = transactionCommandCounter.Count;

            // Renew and fence the claimed attempt in the same transaction as
            // all snapshot and run updates. If another worker took over, none
            // of this worker's prepared data may be published.
            var fenceNow = DateTimeOffset.UtcNow;
            CountTransactionCommand();
            var fenced = await db.MoodleSyncStates
                .Where(state => state.Id == work.StateId &&
                                state.Status == "running" &&
                                state.AttemptCount == work.AttemptCount &&
                                state.LeaseUntil != null &&
                                state.LeaseUntil > fenceNow)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(state => state.LeaseUntil, fenceNow.AddMinutes(_options.LeaseMinutes))
                    .SetProperty(state => state.UpdatedAt, fenceNow), cancellationToken);
            if (fenced != 1)
            {
                throw new LostSyncLeaseException("A sincronização perdeu o lease antes da publicação.");
            }

            foreach (var staged in syncResult.Snapshots)
            {
                await PublishSnapshotAsync(db, work, staged, cancellationToken);
            }
            await ApplyCompletedCourseCleanupAsync(db, work, syncResult.CompletedCourseCleanup, cancellationToken);

            var state = await db.MoodleSyncStates.SingleAsync(item => item.Id == work.StateId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var publishedDataset = work.Dataset is MoodleSnapshotDatasets.Connection or MoodleSnapshotDatasets.Courses
                ? MoodleSnapshotDatasets.Courses
                : work.Dataset;
            CountTransactionCommand();
            var publishedSnapshot = await db.MoodleSnapshots
                .AsNoTracking()
                .Where(item => item.OwnerId == work.OwnerId &&
                               item.ConnectionId == work.ConnectionId &&
                               item.SnapshotType == publishedDataset &&
                               item.CourseId == (work.CourseId ?? string.Empty))
                .Select(item => new { item.PayloadHash, item.PayloadJson, item.RecordCount })
                .SingleOrDefaultAsync(cancellationToken);
            state.Status = syncResult.Partial ? "partial" : "succeeded";
            state.LastCompletedAt = now;
            state.NextSyncAt = GetNextAutomaticSyncAt(work.Dataset, now);
            state.LastError = null;
            state.LeaseUntil = null;
            state.RecordsSynced = syncResult.Records;
            state.ForceRequested = false;
            state.UpdatedAt = now;
            item.Status = syncResult.Partial ? "partial" : "succeeded";
            item.FinishedAt = now;
            item.DurationMs = (long)Stopwatch.GetElapsedTime(syncStartedAt).TotalMilliseconds;
            item.RecordCount = syncResult.Records;
            item.PayloadHash = publishedSnapshot?.PayloadHash;
            item.PayloadSizeBytes = publishedSnapshot is null ? 0 : Encoding.UTF8.GetByteCount(publishedSnapshot.PayloadJson);
            run.Status = syncResult.Partial ? "partial" : "succeeded";
            run.FinishedAt = now;
            run.ItemsSucceeded = syncResult.Partial ? 0 : 1;
            run.ItemsFailed = syncResult.Partial ? 1 : 0;
            run.RecordsSynced = syncResult.Records;
            run.DurationMs = (long)Stopwatch.GetElapsedTime(syncStartedAt).TotalMilliseconds;
            CountTransactionCommand();
            await db.SaveChangesAsync(cancellationToken);
            transactionCommandCounter.Increment();
            await transaction.CommitAsync(cancellationToken);
            metrics.RecordPublish(
                work.Dataset,
                Stopwatch.GetElapsedTime(publishStartedAt).TotalMilliseconds,
                Math.Max(0, transactionCommandCounter.Count - publishCommandCountBefore));
            publishMetricRecorded = true;

            var snapshotStore = scope.ServiceProvider.GetRequiredService<IMoodleSnapshotStore>();
            string[] invalidatedDatasets = work.Dataset switch
            {
                MoodleSnapshotDatasets.Connection => [MoodleSnapshotDatasets.Courses],
                // A submissions read also refreshes activities, participants,
                // and the derived gradebook data.
                MoodleSnapshotDatasets.Submissions => [
                    MoodleSnapshotDatasets.Submissions,
                    MoodleSnapshotDatasets.Activities,
                    MoodleSnapshotDatasets.Students,
                    MoodleSnapshotDatasets.Gradebook],
                _ => [work.Dataset],
            };
            foreach (var dataset in invalidatedDatasets)
            {
                snapshotStore.Invalidate(work.OwnerId, work.ConnectionAlias, dataset, work.CourseId ?? string.Empty);
                metrics.RecordRefresh(dataset);
            }

            logger.LogInformation(
                "Moodle snapshot sync completed. ConnectionKey={ConnectionKey} Dataset={Dataset} Records={Records}",
                MoodleCallDiagnosticCatalog.ConnectionKey(work.ConnectionId),
                work.Dataset,
                syncResult.Records);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transaction is not null)
            {
                try { CountTransactionCommand(); await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            await MarkCancelledAsync(claimedWork, runId, runItemId, runStartedAt);
            throw;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                try { CountTransactionCommand(); await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            await MarkFailedAsync(request, exception, runId, runItemId, runStartedAt, claimedWork, cancellationToken);
            var failureCallId = exception is MoodleApiException moodleFailure &&
                                Guid.TryParse(moodleFailure.AuditId, out var failureAuditGuid)
                ? failureAuditGuid.ToString("N")
                : null;
            var failureErrorCode = MoodleErrorContract.NormalizeCode(exception is MoodleApiException apiFailure
                ? apiFailure.ErrorCode
                : MoodleErrorContract.Unexpected);
            logger.LogWarning(
                "Moodle snapshot sync failed. CallId={CallId} ErrorCode={ErrorCode} Dataset={Dataset}",
                failureCallId,
                failureErrorCode,
                claimedWork?.Dataset ?? request.Dataset);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
                _activeTransactionCommands.Value = null;
                metrics.RecordTransaction(
                    claimedWork?.Dataset ?? request.Dataset,
                    Stopwatch.GetElapsedTime(transactionStartedAt).TotalMilliseconds,
                    transactionCommandCounter?.Count ?? 0);
                if (publishStartedAt != 0 && !publishMetricRecorded && transactionCommandCounter is not null)
                {
                    metrics.RecordPublish(
                        claimedWork?.Dataset ?? request.Dataset,
                        Stopwatch.GetElapsedTime(publishStartedAt).TotalMilliseconds,
                        Math.Max(0, transactionCommandCounter.Count - publishCommandCountBefore));
                }
            }
            if (syncStartedAt != 0 && !syncMetricRecorded)
            {
                metrics.RecordSyncDuration(request.Dataset, Stopwatch.GetElapsedTime(syncStartedAt).TotalMilliseconds);
            }
            if (leaseHeartbeatCancellation is not null)
            {
                leaseHeartbeatCancellation.Cancel();
                if (leaseHeartbeat is not null)
                {
                    try
                    {
                        await leaseHeartbeat;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                leaseHeartbeatCancellation.Dispose();
            }

            lock (_gate)
            {
                _pending.Remove(key);
                _enqueuedAtTimestamp.Remove(key);
            }
        }
    }

    private async Task RenewLeaseAsync(SyncWork work, CancellationToken cancellationToken)
    {
        var renewalInterval = TimeSpan.FromMinutes(Math.Max(1, _options.LeaseMinutes / 3d));
        using var timer = new PeriodicTimer(renewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
                var now = DateTimeOffset.UtcNow;
                await db.MoodleSyncStates
                    .Where(item => item.Id == work.StateId &&
                                   item.Status == "running" &&
                                   item.AttemptCount == work.AttemptCount &&
                                   item.LeaseUntil != null &&
                                   item.LeaseUntil > now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.LeaseUntil, now.AddMinutes(_options.LeaseMinutes))
                        .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            logger.LogWarning("Não foi possível renovar o lease da sincronização Moodle. FailureReason={FailureReason}", "database_error");
        }
    }

    private async Task<SyncWork?> TryClaimAsync(
        MoodleSnapshotSyncRequest request,
        CancellationToken cancellationToken,
        Action? onDatabaseCommand = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
        {
            var identityStartedAt = Stopwatch.GetTimestamp();
            long identityCommands = 0;
            try
            {
                request = request with
                {
                    ConnectionId = await MoodleConnectionIdentity.ResolveAsync(
                        db,
                        request.OwnerId,
                        request.ClientId,
                        request.ConnectionAlias,
                        cancellationToken,
                        () => identityCommands++)
                };
            }
            finally
            {
                metrics.RecordIdentityResolution(
                    request.Dataset,
                    Stopwatch.GetElapsedTime(identityStartedAt).TotalMilliseconds,
                    identityCommands);
            }
        }
        onDatabaseCommand?.Invoke();
        var state = await db.MoodleSyncStates.AsNoTracking().SingleOrDefaultAsync(
            item => item.OwnerId == request.OwnerId &&
                    (item.ConnectionId == request.ConnectionId ||
                     (item.ConnectionId == string.Empty && item.ConnectionAlias == request.ConnectionAlias)) &&
                    item.Dataset == request.Dataset &&
                    item.CourseId == (request.CourseId ?? string.Empty),
            cancellationToken);
        if (state is null || state.NextSyncAt is not { } next || next > DateTimeOffset.UtcNow)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (MoodleSyncLeasePolicy.IsActive(state, now))
        {
            return null;
        }

        var attemptCount = state.AttemptCount + 1;
        var force = request.Force || state.ForceRequested;
        onDatabaseCommand?.Invoke();
        var updated = await db.MoodleSyncStates
            .Where(item => item.Id == state.Id &&
                           (item.Status != "running" || item.LeaseUntil == null || item.LeaseUntil <= now) &&
                           (item.LeaseUntil == null || item.LeaseUntil <= now) &&
                           item.NextSyncAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "running")
                .SetProperty(item => item.LastStartedAt, now)
                .SetProperty(item => item.LastAttemptAt, now)
                .SetProperty(item => item.LeaseUntil, now.AddMinutes(_options.LeaseMinutes))
                .SetProperty(item => item.AttemptCount, attemptCount)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (updated != 1)
        {
            return null;
        }

        return new SyncWork(
            state.Id,
            state.OwnerId,
            state.ClientId,
            state.ConnectionId,
            state.ConnectionAlias,
            state.UserExternalId,
            state.Dataset,
            string.IsNullOrWhiteSpace(state.CourseId) ? null : state.CourseId,
            null,
            force,
            attemptCount,
            request.WorkPolicy);
    }

    private static Task<MoodleSnapshotRunEntity> StartRunAsync(
        ConnectorDbContext db,
        SyncWork work,
        string trigger,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new MoodleSnapshotRunEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = work.OwnerId,
            ConnectionId = work.ConnectionId,
            ConnectionAlias = work.ConnectionAlias,
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "scheduled" : trigger.Trim().ToLowerInvariant(),
            WorkerId = WorkerId,
            SynchronizerVersion = typeof(MoodleSnapshotSyncQueue).Assembly.GetName().Version?.ToString() ?? "unknown",
            StartedAt = now,
            CreatedAt = now,
        };
        db.MoodleSnapshotRuns.Add(run);
        return Task.FromResult(run);
    }

    private async Task MarkCancelledAsync(
        SyncWork? work,
        Guid? runId,
        Guid? runItemId,
        DateTimeOffset runStartedAt)
    {
        if (work is null && runId is null && runItemId is null)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            var now = DateTimeOffset.UtcNow;
            if (work is { } claimedWork)
            {
                await db.MoodleSyncStates
                    .Where(item => item.Id == claimedWork.StateId &&
                                   item.Status == "running" &&
                                   item.AttemptCount == claimedWork.AttemptCount &&
                                   item.LeaseUntil != null &&
                                   item.LeaseUntil > now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, "cancelled")
                        .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(item => item.NextSyncAt, now)
                        .SetProperty(item => item.UpdatedAt, now), CancellationToken.None);
            }
            if (runId is { } persistedRunId)
            {
                var run = await db.MoodleSnapshotRuns.SingleOrDefaultAsync(item => item.Id == persistedRunId);
                if (run is not null)
                {
                    run.Status = "cancelled";
                    run.FinishedAt = now;
                    run.DurationMs = Math.Max(0, (long)(now - runStartedAt).TotalMilliseconds);
                }
            }
            if (runItemId is { } persistedRunItemId)
            {
                var item = await db.MoodleSnapshotRunItems.SingleOrDefaultAsync(item => item.Id == persistedRunItemId);
                if (item is not null)
                {
                    item.Status = "cancelled";
                    item.FinishedAt = now;
                    item.DurationMs = Math.Max(0, (long)(now - item.StartedAt).TotalMilliseconds);
                }
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            logger.LogWarning("Não foi possível persistir o cancelamento da sincronização Moodle. FailureReason={FailureReason}", "database_error");
        }
    }

    private async Task MarkFailedAsync(
        MoodleSnapshotSyncRequest request,
        Exception exception,
        Guid? runId,
        Guid? runItemId,
        DateTimeOffset runStartedAt,
        SyncWork? claimedWork,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            var now = DateTimeOffset.UtcNow;
            var state = claimedWork is { } work
                ? await db.MoodleSyncStates.SingleOrDefaultAsync(
                    item => item.Id == work.StateId &&
                            item.Status == "running" &&
                            item.AttemptCount == work.AttemptCount &&
                            item.LeaseUntil != null &&
                            item.LeaseUntil > now,
                    cancellationToken)
                : await db.MoodleSyncStates.SingleOrDefaultAsync(
                    item => item.OwnerId == request.OwnerId &&
                            (item.ConnectionId == request.ConnectionId ||
                             (item.ConnectionId == string.Empty && item.ConnectionAlias == request.ConnectionAlias)) &&
                            item.Dataset == request.Dataset &&
                            item.CourseId == (request.CourseId ?? string.Empty) &&
                            (item.Status != "running" || item.LeaseUntil == null || item.LeaseUntil <= now),
                    cancellationToken);
            var descriptor = MoodleErrorContract.Describe(exception);
            var safeError = descriptor.Message;
            if (exception is MoodleApiException moodleFailure && Guid.TryParse(moodleFailure.AuditId, out var auditGuid))
            {
                safeError = $"{safeError} [callId={auditGuid:N}]";
            }
            safeError = safeError.Length > 4000 ? safeError[..4000] : safeError;
            if (state is not null)
            {
                var permanentFailure = IsPermanentSyncFailure(descriptor.ErrorCode);
                var retryCapSeconds = permanentFailure
                    ? TimeSpan.FromHours(24).TotalSeconds
                    : TimeSpan.FromHours(1).TotalSeconds;
                var exponentialSeconds = 30 * Math.Pow(2, Math.Max(0, state.AttemptCount - 1));
                var seconds = permanentFailure
                    ? retryCapSeconds
                    : Math.Min(retryCapSeconds, exponentialSeconds) * (0.75 + Random.Shared.NextDouble() * 0.5);
                if (claimedWork is { } failedWork && IsCourseOverviewWorkPolicy(failedWork.WorkPolicy))
                {
                    seconds = Math.Max(seconds, TimeSpan.FromMinutes(5).TotalSeconds);
                }
                state.Status = "failed";
                state.LastError = safeError;
                state.NextSyncAt = now.AddSeconds(seconds);
                state.LeaseUntil = null;
                state.UpdatedAt = now;
            }
            if (runId is { } persistedRunId)
            {
                var run = await db.MoodleSnapshotRuns.SingleOrDefaultAsync(item => item.Id == persistedRunId, cancellationToken);
                if (run is not null)
                {
                    run.Status = "failed";
                    run.FinishedAt = now;
                    run.ItemsFailed = 1;
                    run.DurationMs = Math.Max(0, (long)(now - runStartedAt).TotalMilliseconds);
                    run.Error = safeError;
                }
            }
            if (runItemId is { } persistedItemId)
            {
                var item = await db.MoodleSnapshotRunItems.SingleOrDefaultAsync(item => item.Id == persistedItemId, cancellationToken);
                if (item is not null)
                {
                    item.Status = "failed";
                    item.FinishedAt = now;
                    item.DurationMs = Math.Max(0, (long)(now - item.StartedAt).TotalMilliseconds);
                    item.Error = safeError;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception persistenceException)
        {
            logger.LogError(
                "Falha ao persistir o estado de erro da sincronização Moodle. FailureReason={FailureReason}",
                "database_error");
        }
    }

    private static bool IsPermanentSyncFailure(string errorCode) => errorCode switch
    {
        MoodleErrorContract.ConnectionNotFound or
        MoodleErrorContract.ConnectionDisabled or
        MoodleErrorContract.TokenMissing or
        MoodleErrorContract.TokenDecryptionFailed or
        MoodleErrorContract.AuthenticationFailed or
        MoodleErrorContract.FunctionNotAllowed or
        MoodleErrorContract.PermissionDenied or
        MoodleErrorContract.CourseNotFound => true,
        _ => false,
    };

    private async Task<SnapshotSyncResult> SyncAsync(
        IServiceProvider services,
        SyncWork work,
        CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ConnectorDbContext>();
        var coursesGateway = services.GetRequiredService<IMoodleCoursesGateway>();
        var contentsGateway = services.GetRequiredService<IMoodleCourseContentsGateway>();
        var participantsGateway = services.GetRequiredService<IMoodleParticipantsGateway>();
        var submissionsGateway = services.GetRequiredService<IMoodleAssignmentSubmissionsGateway>();
        var assignmentSettingsGateway = services.GetRequiredService<IMoodleAssignmentSettingsGateway>();
        var assignmentGradeReadGateway = services.GetRequiredService<IMoodleAssignmentGradeReadGateway>();
        var gradebookGateway = services.GetRequiredService<IMoodleGradebookGateway>();
        var stagedSnapshots = new List<StagedSnapshot>();
        CompletedCourseCleanup? completedCourseCleanup = null;

        if (work.Dataset is MoodleSnapshotDatasets.Connection or MoodleSnapshotDatasets.Courses)
        {
            var courseResult = await ReadCoursesAsync(coursesGateway, work.UserExternalId, cancellationToken);
            StageSnapshot(stagedSnapshots, work, MoodleSnapshotDatasets.Courses, string.Empty, courseResult.Items, "warm", false,
                completeOverride: !courseResult.Partial);

            if (work.Dataset == MoodleSnapshotDatasets.Connection && !courseResult.Partial)
            {
                var courseIds = courseResult.Items.Select(course => course.CourseId).ToArray();
                completedCourseCleanup = new CompletedCourseCleanup(courseIds, DateTimeOffset.UtcNow.AddDays(-7));
            }

            return new SnapshotSyncResult(courseResult.Items.Count, courseResult.Partial, stagedSnapshots, completedCourseCleanup);
        }

        if (string.IsNullOrWhiteSpace(work.CourseId))
        {
            return new SnapshotSyncResult(0, false, stagedSnapshots, completedCourseCleanup);
        }

        var courseSummary = await ReadCourseFromSnapshotAsync(db, work, cancellationToken);
        if (courseSummary is null && !IsCourseOverviewWorkPolicy(work.WorkPolicy))
        {
            courseSummary = await coursesGateway.GetMyCourseAsync(work.UserExternalId, work.CourseId, cancellationToken);
        }
        if (courseSummary is null)
        {
            return new SnapshotSyncResult(
                0,
                IsCourseOverviewWorkPolicy(work.WorkPolicy),
                stagedSnapshots,
                completedCourseCleanup);
        }

        var nowForCourse = DateTimeOffset.UtcNow;
        var finishedCourse = courseSummary.EndDate is not null && courseSummary.EndDate < nowForCourse;
        var gradebookFrozen = courseSummary.EndDate is { } endDate &&
            endDate.AddDays(_options.GradebookFreezeAfterCourseEndDays) <= nowForCourse;
        var frozenCheckDataset = work.Dataset switch
        {
            MoodleSnapshotDatasets.Activities or
            MoodleSnapshotDatasets.Gradebook or
            MoodleSnapshotDatasets.Submissions => work.Dataset,
            _ => null,
        };
        var snapshotShouldBeFrozen = work.Dataset switch
        {
            MoodleSnapshotDatasets.Activities => finishedCourse,
            MoodleSnapshotDatasets.Gradebook or MoodleSnapshotDatasets.Submissions => gradebookFrozen,
            _ => false,
        };
        if (frozenCheckDataset is not null)
        {
            CountTransactionCommand();
        }
        var existingSnapshot = frozenCheckDataset is null
            ? null
            : await db.MoodleSnapshots.AsNoTracking().SingleOrDefaultAsync(
                item => item.OwnerId == work.OwnerId &&
                        (item.ConnectionId == work.ConnectionId ||
                         (item.ConnectionId == string.Empty && item.ConnectionAlias == work.ConnectionAlias)) &&
                        item.SnapshotType == frozenCheckDataset &&
                        item.CourseId == courseSummary.CourseId,
                cancellationToken);
        if (snapshotShouldBeFrozen &&
            existingSnapshot?.IsFrozen == true &&
            // FrozenAt was introduced with the post-course freeze window. A
            // legacy gradebook/submissions head has no proof that it passed
            // the window, so refresh it once instead of treating it as
            // immutable forever.
            (work.Dataset is not (MoodleSnapshotDatasets.Gradebook or MoodleSnapshotDatasets.Submissions) ||
                existingSnapshot.FrozenAt is not null) &&
            !work.Force)
        {
            return new SnapshotSyncResult(existingSnapshot.RecordCount, false, stagedSnapshots, completedCourseCleanup);
        }

        var partial = false;
        var records = 0;
        switch (work.Dataset)
        {
            case MoodleSnapshotDatasets.Activities:
            {
                var contents = await contentsGateway.GetCourseContentsAsync(
                    work.UserExternalId,
                    courseSummary.CourseId,
                    moduleTypes: [],
                    includeHidden: false,
                    onlyWithFiles: false,
                    cancellationToken);
                StageSnapshot(stagedSnapshots, work, MoodleSnapshotDatasets.Activities, courseSummary.CourseId, contents, finishedCourse ? "cold" : "hot", finishedCourse);
                records = CountRecords(contents);
                partial = false;
                break;
            }
            case MoodleSnapshotDatasets.Students:
            {
                var overviewSync = work.WorkPolicy == MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1;
                if (overviewSync && work.Dataset != MoodleSnapshotDatasets.Students)
                {
                    throw new InvalidOperationException("A política do painel só pode atualizar o snapshot Students.");
                }

                var students = overviewSync
                    ? await participantsGateway.GetCourseParticipantsAsync(
                        work.UserExternalId,
                        courseSummary.CourseId,
                        ParticipantStatusFilter.Active,
                        page: 1,
                        pageSize: 500,
                        studentsOnly: true,
                        includeEmail: false,
                        groupId: null,
                        cancellationToken)
                    : await ReadAllStudentsAsync(
                        participantsGateway,
                        work.UserExternalId,
                        courseSummary.CourseId,
                        _options.ParticipantPageSize,
                        _options.MaxParticipantPages,
                        cancellationToken);
                var complete = !students.HasMore;
                var preserveCompleteHead = false;
                if (overviewSync && !complete)
                {
                    preserveCompleteHead = await db.MoodleSnapshots.AsNoTracking().AnyAsync(
                        item => item.OwnerId == work.OwnerId &&
                                item.ConnectionId == work.ConnectionId &&
                                item.SnapshotType == MoodleSnapshotDatasets.Students &&
                                item.CourseId == courseSummary.CourseId && item.IsComplete,
                        cancellationToken);
                }

                if (!preserveCompleteHead)
                {
                    StageSnapshot(stagedSnapshots, work, MoodleSnapshotDatasets.Students, courseSummary.CourseId,
                        students, "hot", false, completeOverride: complete);
                }
                records = CountRecords(students);
                partial = !complete;
                break;
            }
            case MoodleSnapshotDatasets.Gradebook:
            {
                var overviewGradebookSync = work.WorkPolicy == MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1;
                var students = await ReadAllStudentsAsync(
                    participantsGateway,
                    work.UserExternalId,
                    courseSummary.CourseId,
                    overviewGradebookSync ? 500 : _options.ParticipantPageSize,
                    overviewGradebookSync ? 1 : _options.MaxParticipantPages,
                    cancellationToken);
                var studentIds = students.Participants
                    .Select(student => student.UserId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var individualReadConcurrency = overviewGradebookSync
                    ? Math.Min(2, _options.IndividualGradebookConcurrency)
                    : _options.IndividualGradebookConcurrency;
                CourseGradebookSnapshot gradebook;
                try
                {
                    gradebook = await gradebookGateway.GetCourseGradebookAsync(
                        courseSummary.CourseId,
                        studentIds,
                        groupId: null,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A capability error on the bulk endpoint must not turn a
                    // useful snapshot into a failed run. Fall back to bounded
                    // individual reads and publish explicit coverage.
                    gradebook = await ReadIndividualGradebooksAsync(
                        gradebookGateway,
                        courseSummary.CourseId,
                        studentIds,
                        exception,
                        individualReadConcurrency,
                        cancellationToken);
                }
                if (gradebook.Coverage.SourceMode == "bulk" &&
                    (gradebook.Coverage.MissingStudentIds?.Count ?? 0) > 0)
                {
                    var missingGradebooks = await ReadIndividualGradebooksAsync(
                        gradebookGateway,
                        courseSummary.CourseId,
                        gradebook.Coverage.MissingStudentIds ?? [],
                        new InvalidOperationException("bulk_missing_requested_users"),
                        individualReadConcurrency,
                        cancellationToken);
                    gradebook = MergeGradebookSnapshots(gradebook, missingGradebooks, studentIds);
                }
                if (students.HasMore)
                {
                    gradebook = gradebook with
                    {
                        Coverage = gradebook.Coverage with
                        {
                            IsComplete = false,
                            Truncated = true,
                            Warnings = (gradebook.Coverage.Warnings ?? [])
                                .Concat(["participants_truncated"])
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray(),
                        }
                    };
                }
                var gradebookComplete = !students.HasMore && gradebook.Coverage.IsComplete;
                var preserveCompleteGradebookHead = overviewGradebookSync && !gradebookComplete &&
                    await db.MoodleSnapshots.AsNoTracking().AnyAsync(
                        item => item.OwnerId == work.OwnerId &&
                                item.ConnectionId == work.ConnectionId &&
                                item.SnapshotType == MoodleSnapshotDatasets.Gradebook &&
                                item.CourseId == courseSummary.CourseId && item.IsComplete,
                        cancellationToken);
                if (!preserveCompleteGradebookHead)
                {
                    StageSnapshot(
                        stagedSnapshots,
                        work,
                        MoodleSnapshotDatasets.Gradebook,
                        courseSummary.CourseId,
                        gradebook,
                        gradebookFrozen ? "cold" : "hot",
                        gradebookFrozen,
                        completeOverride: gradebookComplete);
                }
                records = CountRecords(gradebook);
                partial = !gradebookComplete;
                break;
            }
            case MoodleSnapshotDatasets.Groups:
            {
                var groups = await participantsGateway.GetCourseGroupsAsync(work.UserExternalId, courseSummary.CourseId, cancellationToken);
                StageSnapshot(stagedSnapshots, work, MoodleSnapshotDatasets.Groups, courseSummary.CourseId, groups, "warm", false);
                records = CountRecords(groups);
                break;
            }
            case MoodleSnapshotDatasets.Submissions:
            {
                var contents = await contentsGateway.GetCourseContentsAsync(
                    work.UserExternalId,
                    courseSummary.CourseId,
                    moduleTypes: [],
                    includeHidden: false,
                    onlyWithFiles: false,
                    cancellationToken);
                var participants = await ReadAllStudentsAsync(
                    participantsGateway,
                    work.UserExternalId,
                    courseSummary.CourseId,
                    _options.ParticipantPageSize,
                    _options.MaxParticipantPages,
                    cancellationToken);
                var assignmentIds = contents.Sections
                    .SelectMany(section => section.Modules)
                    .Where(module =>
                        string.Equals(module.ModuleType, "assign", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(module.InstanceId))
                    .Select(module => module.InstanceId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var batches = new List<AssignmentSubmissionsBatch>();
                foreach (var assignmentChunk in assignmentIds.Chunk(_options.AssignmentBatchSize))
                {
                    var chunk = await submissionsGateway.GetAssignmentSubmissionsBatchAsync(
                        work.UserExternalId,
                        assignmentChunk,
                        status: null,
                        since: null,
                        before: null,
                        cancellationToken);
                    batches.AddRange(chunk);
                }
                IReadOnlyDictionary<string, AssignmentSettingsSummary> assignmentSettings =
                    new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal);
                if (assignmentIds.Length > 0)
                {
                    try
                    {
                        assignmentSettings = await assignmentSettingsGateway.GetCourseAssignmentSettingsAsync(
                            work.UserExternalId,
                            courseSummary.CourseId,
                            cancellationToken);
                    }
                    catch
                    {
                        // The submissions payload remains useful when Moodle
                        // does not expose the optional grade configuration.
                    }
                }

                // mod_assign_get_grades returns all grade rows for an assignment
                // in one call. Keep the result keyed by assignment so the
                // projector can persist current grade/feedback and mark grade
                // coverage explicitly instead of guessing from submission
                // status alone.
                var existingGrades = new Dictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>(
                    StringComparer.OrdinalIgnoreCase);
                var gradesPartial = false;
                var feedbackResults = await assignmentGradeReadGateway.GetExistingGradesBatchAsync(
                    work.UserExternalId,
                    assignmentIds,
                    participants.Participants.Select(participant => participant.UserId).ToArray(),
                    cancellationToken);
                foreach (var result in feedbackResults)
                {
                    existingGrades[result.AssignmentId] = result.Grades;
                    if (!string.IsNullOrWhiteSpace(result.ErrorCode))
                    {
                        gradesPartial = true;
                    }
                }

                // A gateway implementation may omit an assignment result
                // without throwing. Treat the omission as incomplete coverage
                // rather than publishing a falsely complete grade snapshot.
                if (feedbackResults.Select(result => result.AssignmentId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .Count != assignmentIds.Length)
                {
                    gradesPartial = true;
                }

                var extensions = await AssignmentSubmissionLatenessProjection.ReadExtensionsAsync(
                    submissionsGateway, work.UserExternalId, assignmentIds, cancellationToken);
                var effectiveDeadlineRequests = AssignmentSubmissionLatenessProjection.EffectiveDeadlineRequests(batches);
                var effectiveDeadlines = await AssignmentSubmissionLatenessProjection.ReadEffectiveDeadlinesAsync(
                    submissionsGateway, work.UserExternalId, effectiveDeadlineRequests, cancellationToken);
                var snapshot = AssignmentSubmissionSnapshotProjector.Build(
                    contents,
                    participants,
                    batches,
                    assignmentSettings,
                    existingGrades,
                    extensions,
                    effectiveDeadlines);
                StageSnapshot(stagedSnapshots, work, MoodleSnapshotDatasets.Activities, courseSummary.CourseId, contents, finishedCourse ? "cold" : "hot", finishedCourse);
                StageSnapshot(stagedSnapshots, work, MoodleSnapshotDatasets.Students, courseSummary.CourseId, participants, "hot", false);
                var submissionsComplete = !participants.HasMore && !gradesPartial && snapshot.Assignments.All(assignment =>
                    assignment.IsComplete &&
                    assignment.LatenessCoverageComplete &&
                    (assignment.Coverage is null || assignment.Coverage.NeedsGradingComplete));
                StageSnapshot(
                    stagedSnapshots,
                    work,
                    MoodleSnapshotDatasets.Submissions,
                    courseSummary.CourseId,
                    snapshot,
                    gradebookFrozen ? "cold" : "hot",
                    gradebookFrozen,
                    completeOverride: submissionsComplete);
                records = CountRecords(snapshot);
                partial = participants.HasMore || gradesPartial || snapshot.Assignments.Any(assignment =>
                    !assignment.IsComplete || !assignment.LatenessCoverageComplete ||
                    assignment.Coverage is not null && !assignment.Coverage.NeedsGradingComplete);
                break;
            }
            default:
                throw new InvalidOperationException($"Dataset de snapshot desconhecido: {work.Dataset}.");
        }

        return new SnapshotSyncResult(records, partial, stagedSnapshots, completedCourseCleanup);
    }

    private async Task<CourseReadResult> ReadCoursesAsync(
        IMoodleCoursesGateway coursesGateway,
        string userExternalId,
        CancellationToken cancellationToken)
    {
        var courses = new List<CourseSummary>();
        var partial = true;
        for (var page = 1; page <= _options.MaxCoursePages; page++)
        {
            var result = await coursesGateway.GetMyCoursesAsync(userExternalId, _options.CoursePageSize, page, cancellationToken);
            courses.AddRange(result.Items);
            if (!result.HasNextPage)
            {
                partial = false;
                break;
            }
        }

        if (partial)
        {
            logger.LogWarning(
                "Moodle course snapshot reached the configured page cap. CourseCount={CourseCount} PageSize={PageSize} MaxPages={MaxPages}",
                courses.Count,
                _options.CoursePageSize,
                _options.MaxCoursePages);
        }

        return new CourseReadResult(courses, partial);
    }

    private static async Task<CourseParticipantsPage> ReadAllStudentsAsync(
        IMoodleParticipantsGateway participantsGateway,
        string userExternalId,
        string courseId,
        int pageSize,
        int maxPages,
        CancellationToken cancellationToken)
    {
        var participants = new List<CourseParticipantSummary>();
        var seenParticipantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 1;
        var hasMore = false;
        ParticipantClassificationDiagnostics? diagnostics = null;
        while (page <= maxPages)
        {
            var result = await participantsGateway.GetCourseParticipantsAsync(
                userExternalId,
                courseId,
                ParticipantStatusFilter.Active,
                page,
                pageSize,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken: cancellationToken);
            var added = 0;
            foreach (var participant in result.Participants)
            {
                if (seenParticipantIds.Add(participant.UserId))
                {
                    participants.Add(participant);
                    added++;
                }
            }
            diagnostics = result.ClassificationDiagnostics;
            hasMore = result.HasMore;
            if (!result.HasMore || result.Participants.Count == 0 || added == 0)
            {
                break;
            }
            page++;
        }

        if (page > maxPages && hasMore)
        {
            hasMore = true;
        }

        return new CourseParticipantsPage(
            CourseId: courseId,
            Page: 1,
            PageSize: pageSize,
            StatusFilter: ParticipantStatusFilter.Active,
            StudentsOnly: true,
            IncludeEmail: false,
            HasMore: hasMore,
            Participants: participants,
            ClassificationDiagnostics: diagnostics);
    }

    private static async Task<CourseGradebookSnapshot> ReadIndividualGradebooksAsync(
        IMoodleGradebookGateway gradebookGateway,
        string courseId,
        IReadOnlyCollection<string> studentIds,
        Exception bulkException,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        var gradebooks = new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var errors = new List<string>();
        var warnings = new List<string> { $"bulk:{bulkException.GetType().Name}" };
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var reads = studentIds.Select(async studentId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var gradebook = await gradebookGateway.GetStudentGradebookAsync(courseId, studentId, cancellationToken);
                lock (gradebooks) gradebooks[studentId] = gradebook;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                lock (missing)
                {
                    missing.Add(studentId);
                    errors.Add(studentId);
                    warnings.Add($"student_read_failed:{exception.GetType().Name}");
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(reads);
        return new CourseGradebookSnapshot(
            courseId,
            gradebooks,
            new GradebookSnapshotCoverage(
                "individual_fallback",
                studentIds.Count,
                gradebooks.Count,
                missing.Count == 0,
                false,
                missing,
                warnings)
            {
                ErrorStudentIds = errors,
            })
            .WithCanonicalProjection();
    }

    private static CourseGradebookSnapshot MergeGradebookSnapshots(
        CourseGradebookSnapshot bulk,
        CourseGradebookSnapshot fallback,
        IReadOnlyCollection<string> requestedStudentIds)
    {
        var gradebooks = new Dictionary<string, CourseGradebook>(bulk.Gradebooks, StringComparer.OrdinalIgnoreCase);
        foreach (var item in fallback.Gradebooks)
        {
            gradebooks[item.Key] = item.Value;
        }

        var missing = requestedStudentIds
            .Where(id => !gradebooks.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var warnings = (bulk.Coverage.Warnings ?? [])
            .Concat(fallback.Coverage.Warnings ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var errors = (bulk.Coverage.ErrorStudentIds ?? [])
            .Concat(fallback.Coverage.ErrorStudentIds ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (bulk with
        {
            Gradebooks = gradebooks,
            Coverage = bulk.Coverage with
            {
                SourceMode = "mixed",
                RequestedStudentCount = requestedStudentIds.Count,
                ReturnedStudentCount = requestedStudentIds.Count - missing.Length,
                IsComplete = missing.Length == 0 && fallback.Coverage.IsComplete,
                MissingStudentIds = missing,
                Warnings = warnings,
                ErrorStudentIds = errors,
            }
        }).WithCanonicalProjection();
    }

    private async Task<CourseSummary?> ReadCourseFromSnapshotAsync(
        ConnectorDbContext db,
        SyncWork work,
        CancellationToken cancellationToken)
    {
        CountTransactionCommand();
        var payload = await db.MoodleSnapshots
            .AsNoTracking()
            .Where(item => item.OwnerId == work.OwnerId &&
                           (item.ConnectionId == work.ConnectionId ||
                            (item.ConnectionId == string.Empty && item.ConnectionAlias == work.ConnectionAlias)) &&
                           item.SnapshotType == MoodleSnapshotDatasets.Courses &&
                           item.CourseId == string.Empty)
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var courses = JsonSerializer.Deserialize<IReadOnlyList<CourseSummary>>(payload, JsonOptions);
            return courses?.FirstOrDefault(course =>
                string.Equals(course.CourseId, work.CourseId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(course.ShortName, work.CourseId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(course.IdNumber, work.CourseId, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void StageSnapshot<T>(
        List<StagedSnapshot> stagedSnapshots,
        SyncWork work,
        string type,
        string courseId,
        T payload,
        string tier,
        bool frozen,
        bool completeOverride = true)
    {
        var serialized = MoodleJsonbSerializer.Serialize(payload, JsonOptions);
        var json = serialized.Json;
        if (serialized.SanitizedCharacters > 0)
        {
            logger.LogWarning(
                "Caracteres incompatíveis com PostgreSQL jsonb removidos do snapshot. Dataset={Dataset} Count={Count}",
                type,
                serialized.SanitizedCharacters);
        }
        var payloadSize = Encoding.UTF8.GetByteCount(json);
        metrics.RecordPayloadBytes(type, payloadSize);
        var payloadLimit = _options.GetPayloadLimit(type);
        if (payloadSize > payloadLimit)
        {
            throw new InvalidOperationException(
                $"O payload do snapshot excede o limite configurado de {payloadLimit} bytes.");
        }
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        stagedSnapshots.Add(new StagedSnapshot(
            type,
            courseId,
            json,
            payloadHash,
            payloadSize,
            tier,
            frozen,
            completeOverride,
            CountRecords(payload),
            payload!));
    }

    private async Task PublishSnapshotAsync(
        ConnectorDbContext db,
        SyncWork work,
        StagedSnapshot staged,
        CancellationToken cancellationToken)
    {
        CountTransactionCommand();
        var entity = await db.MoodleSnapshots.SingleOrDefaultAsync(
            item => item.OwnerId == work.OwnerId &&
                    item.ConnectionId == work.ConnectionId &&
                    item.SnapshotType == staged.Dataset &&
                    item.CourseId == staged.CourseId,
            cancellationToken);
        if (entity is null)
        {
            CountTransactionCommand();
            entity = await db.MoodleSnapshots.SingleOrDefaultAsync(
                item => item.OwnerId == work.OwnerId &&
                        item.ConnectionId == string.Empty &&
                        item.ConnectionAlias == work.ConnectionAlias &&
                        item.SnapshotType == staged.Dataset &&
                        item.CourseId == staged.CourseId,
                cancellationToken);
        }
        if (entity is null)
        {
            entity = new MoodleSnapshotEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = work.OwnerId,
                ConnectionId = work.ConnectionId,
                ConnectionAlias = work.ConnectionAlias,
                SnapshotType = staged.Dataset,
                CourseId = staged.CourseId,
            };
            db.MoodleSnapshots.Add(entity);
        }

        var now = DateTimeOffset.UtcNow;
        if (entity.IsComplete && !IsSnapshotComplete(staged.Payload, staged.CompleteOverride))
        {
            // A partial refresh must never replace the last complete head with
            // a truncated/failed population. Keep the old payload and
            // freshness, but retain the attempt marker for diagnostics and
            // let the durable state remain partial for a subsequent retry.
            entity.LastAttemptAt = now;
            entity.LastError = "partial_refresh_preserved";
            CountTransactionCommand();
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        entity.ConnectionId = work.ConnectionId;
        entity.ConnectionAlias = work.ConnectionAlias;
        var freshInterval = GetFreshInterval(staged.Dataset, staged.Tier, staged.Frozen);
        ApplyQueueSnapshot(
            entity,
            work,
            staged.Json,
            staged.PayloadHash,
            staged.Tier,
            staged.Frozen,
            staged.Dataset,
            now,
            freshInterval,
            staged.Payload,
            staged.CompleteOverride);
        const string savepointName = "moodle_snapshot_upsert";
        var transaction = db.Database.CurrentTransaction;
        if (transaction is not null)
        {
            CountTransactionCommand();
            await transaction.CreateSavepointAsync(savepointName, cancellationToken);
        }

        try
        {
            CountTransactionCommand();
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            if (transaction is not null)
            {
                CountTransactionCommand();
                await transaction.RollbackToSavepointAsync(savepointName, CancellationToken.None);
            }
            db.Entry(entity!).State = EntityState.Detached;
            CountTransactionCommand();
            entity = await db.MoodleSnapshots.SingleOrDefaultAsync(
                item => item.OwnerId == work.OwnerId &&
                        item.ConnectionId == work.ConnectionId &&
                        item.SnapshotType == staged.Dataset &&
                        item.CourseId == staged.CourseId,
                cancellationToken)
                ?? throw new InvalidOperationException("O head do snapshot desapareceu durante o upsert concorrente.");
            if (entity.IsComplete && !IsSnapshotComplete(staged.Payload, staged.CompleteOverride))
            {
                entity.LastAttemptAt = now;
                entity.LastError = "partial_refresh_preserved";
            }
            else
            {
                ApplyQueueSnapshot(
                    entity,
                    work,
                    staged.Json,
                    staged.PayloadHash,
                    staged.Tier,
                    staged.Frozen,
                    staged.Dataset,
                    now,
                    freshInterval,
                    staged.Payload,
                    staged.CompleteOverride);
            }
            CountTransactionCommand();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ApplyCompletedCourseCleanupAsync(
        ConnectorDbContext db,
        SyncWork work,
        CompletedCourseCleanup? cleanup,
        CancellationToken cancellationToken)
    {
        if (cleanup is null)
        {
            return;
        }

        CountTransactionCommand();
        await db.MoodleSnapshots
            .Where(item => item.OwnerId == work.OwnerId &&
                           (item.ConnectionId == work.ConnectionId ||
                            (item.ConnectionId == string.Empty && item.ConnectionAlias == work.ConnectionAlias)) &&
                           item.CourseId != string.Empty &&
                           !cleanup.CourseIds.Contains(item.CourseId) &&
                           item.UpdatedAt < cleanup.Cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        CountTransactionCommand();
        await db.MoodleSyncStates
            .Where(item => item.OwnerId == work.OwnerId &&
                           (item.ConnectionId == work.ConnectionId ||
                            (item.ConnectionId == string.Empty && item.ConnectionAlias == work.ConnectionAlias)) &&
                           item.CourseId != string.Empty &&
                           !cleanup.CourseIds.Contains(item.CourseId) &&
                           item.UpdatedAt < cleanup.Cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private void ApplyQueueSnapshot<T>(
        MoodleSnapshotEntity entity,
        SyncWork work,
        string json,
        string payloadHash,
        string tier,
        bool frozen,
        string type,
        DateTimeOffset now,
        TimeSpan freshInterval,
        T payload,
        bool completeOverride)
    {
        entity.ConnectionId = work.ConnectionId;
        entity.ConnectionAlias = work.ConnectionAlias;
        // Preserve the existing JSON value when the hash did not change. EF
        // then updates freshness/lineage only, avoiding a full JSONB rewrite.
        if (!string.Equals(entity.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
        {
            entity.PayloadJson = json;
        }
        entity.Tier = tier;
        entity.IsFrozen = frozen;
        entity.FrozenAt = frozen ? entity.FrozenAt ?? now : null;
        entity.UpdatedAt = now;
        entity.FreshUntil = now.Add(freshInterval);
        entity.StaleUntil = now.Add(freshInterval + GetStaleWindow(type, tier, frozen));
        entity.LastAttemptAt = now;
        entity.LastError = null;
        entity.PayloadHash = payloadHash;
        entity.LastRunId = work.RunId;
        entity.IsComplete = IsSnapshotComplete(payload, completeOverride);
        entity.RecordCount = CountRecords(payload);
    }

    private static bool IsSnapshotComplete(object? payload, bool completeOverride) =>
        completeOverride && payload switch
        {
            CourseParticipantsPage participants => !participants.HasMore,
            CourseAssignmentSubmissionsSnapshot submissions => submissions.Assignments.All(item => item.IsComplete),
            CourseGradebookSnapshot gradebook => gradebook.Coverage.IsComplete,
            _ => true,
        };

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation;

    private static bool IsSyncStateUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        (postgres.ConstraintName is "IX_moodle_sync_states_scope" or "IX_moodle_sync_states_connection_scope");

    private TimeSpan GetFreshInterval(string type, string tier, bool frozen) =>
        frozen ? TimeSpan.FromDays(3650) : type switch
        {
            MoodleSnapshotDatasets.Courses => TimeSpan.FromDays(2),
            MoodleSnapshotDatasets.Activities => TimeSpan.FromHours(24),
            MoodleSnapshotDatasets.Students or MoodleSnapshotDatasets.Groups => tier.Equals("hot", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(1) : TimeSpan.FromHours(4),
            MoodleSnapshotDatasets.Submissions => TimeSpan.FromMinutes(15),
            MoodleSnapshotDatasets.Gradebook => TimeSpan.FromMinutes(_options.GradebookFreshMinutes),
            _ => TimeSpan.FromHours(1),
        };

    private TimeSpan GetStaleWindow(string type, string tier, bool frozen) =>
        frozen ? TimeSpan.Zero : type switch
        {
            MoodleSnapshotDatasets.Courses => TimeSpan.FromDays(7),
            MoodleSnapshotDatasets.Activities => tier.Equals("cold", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromDays(30) : TimeSpan.FromDays(3),
            MoodleSnapshotDatasets.Students or MoodleSnapshotDatasets.Groups => TimeSpan.FromHours(24),
            MoodleSnapshotDatasets.Submissions => TimeSpan.FromHours(6),
            MoodleSnapshotDatasets.Gradebook => TimeSpan.FromMinutes(_options.GradebookStaleMinutes),
            _ => TimeSpan.FromHours(12),
        };

    private static int CountRecords<T>(T payload) => payload switch
    {
        IReadOnlyCollection<CourseSummary> courses => courses.Count,
        CourseContentsSummary contents => contents.Sections.Sum(section => section.Modules.Count),
        CourseParticipantsPage participants => participants.Participants.Count,
        IReadOnlyCollection<CourseGroupSummary> groups => groups.Count,
        CourseAssignmentSubmissionsSnapshot submissions => submissions.Assignments.Sum(item => item.Submissions.Count),
        CourseGradebookSnapshot gradebook => gradebook.Gradebooks.Sum(item => item.Value.Items.Count),
        _ => 0,
    };

    private static DateTimeOffset GetNextAutomaticSyncAt(string dataset, DateTimeOffset now) => dataset switch
        {
            MoodleSnapshotDatasets.Connection or MoodleSnapshotDatasets.Courses => GetNextBrazilMidnight(now).AddDays(2),
            MoodleSnapshotDatasets.Activities => GetNextBrazilMidnight(now).AddDays(1),
            MoodleSnapshotDatasets.Submissions => now.AddMinutes(30),
            MoodleSnapshotDatasets.Gradebook => now.AddMinutes(15),
            _ => now.Add(dataset == MoodleSnapshotDatasets.Groups ? TimeSpan.FromHours(2) : TimeSpan.FromHours(1)),
        };

    private static DateTimeOffset GetNextBrazilMidnight(DateTimeOffset now)
    {
        var timeZone = ResolveBrazilTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var nextLocalMidnight = localNow.Date.AddDays(1);
        var offset = timeZone.GetUtcOffset(nextLocalMidnight);
        return new DateTimeOffset(nextLocalMidnight, offset).ToUniversalTime();
    }

    private static TimeZoneInfo ResolveBrazilTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
    }

    private bool TryEnqueueSignal(MoodleSnapshotSyncRequest request)
    {
        var key = BuildKey(request);
        var accepted = false;
        lock (_gate)
        {
            if (!_pending.Add(key))
            {
                return false;
            }
            _enqueuedAtTimestamp[key] = Stopwatch.GetTimestamp();

            // Duplicate callers may report an existing signal only after the
            // scheduler has accepted it, not while capacity admission is pending.
            accepted = _scheduler.TryEnqueue(request);
            if (!accepted)
            {
                _pending.Remove(key);
                _enqueuedAtTimestamp.Remove(key);
            }
        }

        if (accepted)
        {
            metrics.RecordQueueEnqueued(request.Dataset);
            return true;
        }

        // The durable row remains due and will be picked up on the next poll.
        metrics.RecordQueueRejected(request.Dataset);
        return false;
    }

    private bool IsSignalPending(MoodleSnapshotSyncRequest request)
    {
        var key = BuildKey(request);
        lock (_gate)
        {
            return _pending.Contains(key);
        }
    }

    private void CountTransactionCommand() => _activeTransactionCommands.Value?.Increment();

    private static MoodleSnapshotSyncRequest Normalize(MoodleSnapshotSyncRequest request)
    {
        var dataset = request.Dataset.Trim().ToLowerInvariant();
        if (dataset is not (MoodleSnapshotDatasets.Connection or MoodleSnapshotDatasets.Courses or MoodleSnapshotDatasets.Activities or MoodleSnapshotDatasets.Students or MoodleSnapshotDatasets.Groups or MoodleSnapshotDatasets.Submissions or MoodleSnapshotDatasets.Gradebook))
        {
            dataset = MoodleSnapshotDatasets.Connection;
        }

        return request with
        {
            ConnectionAlias = request.ConnectionAlias.Trim().ToLowerInvariant(),
            ConnectionId = string.IsNullOrWhiteSpace(request.ConnectionId) ? null : request.ConnectionId.Trim(),
            Trigger = string.IsNullOrWhiteSpace(request.Trigger) ? "scheduled" : request.Trigger.Trim().ToLowerInvariant(),
            Dataset = dataset,
            CourseId = dataset is MoodleSnapshotDatasets.Activities or MoodleSnapshotDatasets.Students or MoodleSnapshotDatasets.Groups or MoodleSnapshotDatasets.Submissions or MoodleSnapshotDatasets.Gradebook
                ? request.CourseId?.Trim()
                : null,
            Priority = Math.Clamp(request.Priority, 0, 1000),
            WorkPolicy = (request.WorkPolicy == MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1 && dataset == MoodleSnapshotDatasets.Students) ||
                         (request.WorkPolicy == MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1 && dataset == MoodleSnapshotDatasets.Gradebook)
                ? request.WorkPolicy
                : MoodleSnapshotWorkPolicies.Default,
        };
    }

    private static bool IsCourseOverviewWorkPolicy(string workPolicy) =>
        workPolicy is MoodleSnapshotWorkPolicies.CourseOverviewStudentsV1 or MoodleSnapshotWorkPolicies.CourseOverviewGradebookV1;

    private static string BuildKey(MoodleSnapshotSyncRequest request) =>
        $"{request.OwnerId}:{request.ConnectionId ?? request.ConnectionAlias}:{request.Dataset}:{request.CourseId}";

    private sealed record SyncWork(
        Guid StateId,
        Guid OwnerId,
        string ClientId,
        string ConnectionId,
        string ConnectionAlias,
        string UserExternalId,
        string Dataset,
        string? CourseId,
        Guid? RunId,
        bool Force,
        int AttemptCount,
        string WorkPolicy);

    private sealed record CourseReadResult(IReadOnlyList<CourseSummary> Items, bool Partial);

    private sealed record StagedSnapshot(
        string Dataset,
        string CourseId,
        string Json,
        string PayloadHash,
        int PayloadSizeBytes,
        string Tier,
        bool Frozen,
        bool CompleteOverride,
        int RecordCount,
        object Payload);

    private sealed record CompletedCourseCleanup(IReadOnlyCollection<string> CourseIds, DateTimeOffset Cutoff);

    private sealed record SnapshotSyncResult(
        int Records,
        bool Partial,
        IReadOnlyList<StagedSnapshot> Snapshots,
        CompletedCourseCleanup? CompletedCourseCleanup);

    private sealed class LostSyncLeaseException(string message) : InvalidOperationException(message);

    private sealed class DatabaseCommandCounter
    {
        private long _count;

        public long Count => Interlocked.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }
}
