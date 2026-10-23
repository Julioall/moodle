using System.Collections.Concurrent;
using System.Threading.Channels;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;
using Moodle.Infrastructure;
using Moodle.Infrastructure.Configuration;
using Npgsql;

namespace Moodle.Presentation;

internal static class DashboardOverviewCache
{
    public static string Pending(Guid ownerId, string connectionAlias) =>
        $"dashboard-metric:{ownerId}:{connectionAlias}:pending";
}

internal sealed record DashboardPendingSnapshotBatch(
    IReadOnlyDictionary<string, MoodleSnapshotEnvelope<CourseParticipantsPage>> Students);

internal static class DashboardPendingSnapshotBatchReader
{
    public static async Task<DashboardPendingSnapshotBatch> ReadAsync(
        IMoodleSnapshotStore snapshotStore,
        Guid ownerId,
        string connectionAlias,
        IReadOnlyCollection<string> courseIds,
        CancellationToken cancellationToken)
    {
        var students = await snapshotStore.GetManyAsync<CourseParticipantsPage>(
            ownerId,
            connectionAlias,
            MoodleSnapshotDatasets.Students,
            courseIds,
            cancellationToken);
        return new DashboardPendingSnapshotBatch(students);
    }
}

internal sealed record DashboardOverviewRefreshRequest(
    Guid OwnerId,
    string ClientId,
    string ConnectionAlias,
    IReadOnlyList<CourseSummary> Courses,
    bool Force = false,
    string? ConnectionId = null);

internal interface IDashboardOverviewRefreshQueue
{
    bool Enqueue(DashboardOverviewRefreshRequest request);
    Task<bool> EnqueueAsync(DashboardOverviewRefreshRequest request, CancellationToken cancellationToken = default);
    bool IsQueued(Guid ownerId, string connectionAlias);
    Task<bool> IsQueuedAsync(Guid ownerId, string connectionAlias, CancellationToken cancellationToken = default);
}

internal sealed class DashboardOverviewRefreshQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<DashboardOverviewRefreshQueue> logger,
    IOptions<MoodleSnapshotOptions> snapshotOptions) : BackgroundService, IDashboardOverviewRefreshQueue
{
    private static readonly string WorkerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly MoodleSnapshotOptions options = snapshotOptions.Value.Normalize();
    private readonly Channel<DashboardOverviewRefreshRequest> channel =
        Channel.CreateBounded<DashboardOverviewRefreshRequest>(new BoundedChannelOptions(snapshotOptions.Value.Normalize().QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly ConcurrentDictionary<string, byte> queued = new(StringComparer.Ordinal);
    private readonly DateTimeOffset applicationStartedAt = DateTimeOffset.UtcNow;

    public bool Enqueue(DashboardOverviewRefreshRequest request) =>
        EnqueueAsync(request).GetAwaiter().GetResult();

    public async Task<bool> EnqueueAsync(
        DashboardOverviewRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) ||
            string.IsNullOrWhiteSpace(request.ConnectionAlias) ||
            request.Courses.Count == 0)
        {
            return false;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var connectionId = string.IsNullOrWhiteSpace(request.ConnectionId)
            ? await MoodleConnectionIdentity.ResolveAsync(db, request.OwnerId, request.ClientId, request.ConnectionAlias, cancellationToken)
            : request.ConnectionId.Trim();
        request = request with { ConnectionId = connectionId };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            // The alias is the durable scope key, while the connection id keeps
            // renamed aliases attached to the same durable state.
            var state = await db.MoodleSyncStates.SingleOrDefaultAsync(item =>
                item.OwnerId == request.OwnerId &&
                (item.ConnectionId == request.ConnectionId ||
                 item.ConnectionAlias == request.ConnectionAlias) &&
                item.Dataset == MoodleSnapshotDatasets.DashboardPending &&
                item.CourseId == string.Empty, cancellationToken);
            if (state is not null && MoodleSyncLeasePolicy.IsActive(state, now))
            {
                return false;
            }

            if (state is not null &&
                state.Status == "pending" &&
                state.NextSyncAt is { } nextSyncAt)
            {
                if (nextSyncAt > now)
                {
                    if (!request.Force)
                    {
                        return false;
                    }

                    // A per-course snapshot can become ready after a dashboard
                    // attempt has scheduled its retry. Let an explicit force from
                    // the screen retry now instead of leaving the user in a
                    // preparatory state for the full backoff window.
                    state.NextSyncAt = now;
                    state.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                }

                var dueKey = GetKey(request.OwnerId, request.ConnectionId!);
                if (!queued.TryAdd(dueKey, 0)) return false;
                if (channel.Writer.TryWrite(request)) return true;
                queued.TryRemove(dueKey, out _);
                return false;
            }

            if (state is null)
            {
                state = new MoodleSyncStateEntity
                {
                    Id = Guid.NewGuid(),
                    OwnerId = request.OwnerId,
                    ConnectionId = request.ConnectionId!,
                    ConnectionAlias = request.ConnectionAlias,
                    Dataset = MoodleSnapshotDatasets.DashboardPending,
                    CourseId = string.Empty,
                };
                db.MoodleSyncStates.Add(state);
            }

            state.ConnectionId = request.ConnectionId!;
            state.ConnectionAlias = request.ConnectionAlias;
            state.ClientId = request.ClientId;
            state.UserExternalId = request.OwnerId.ToString();
            state.Status = "pending";
            state.NextSyncAt = now;
            state.Priority = 5;
            state.LastError = null;
            state.UpdatedAt = now;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsSyncStateUniqueViolation(exception) && attempt == 0)
            {
                // Concurrent dashboard requests can observe an empty scope before
                // either insert commits. Re-read the winner and re-evaluate it.
                db.ChangeTracker.Clear();
                continue;
            }

            var key = GetKey(request.OwnerId, request.ConnectionId!);
            if (!queued.TryAdd(key, 0)) return false;
            if (channel.Writer.TryWrite(request)) return true;
            queued.TryRemove(key, out _);
            return false;
        }

        logger.LogWarning(
            "Não foi possível enfileirar a atualização do dashboard após uma corrida de escopo.");
        return false;
    }

    public bool IsQueued(Guid ownerId, string connectionAlias) =>
        queued.ContainsKey(GetKey(ownerId, connectionAlias));

    public async Task<bool> IsQueuedAsync(Guid ownerId, string connectionAlias, CancellationToken cancellationToken = default)
    {
        if (IsQueued(ownerId, connectionAlias)) return true;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        return await db.MoodleSyncStates.AsNoTracking().AnyAsync(item =>
            item.OwnerId == ownerId && item.ConnectionAlias == connectionAlias &&
            item.Dataset == MoodleSnapshotDatasets.DashboardPending && item.CourseId == string.Empty &&
            (item.Status == "pending" || item.Status == "running"), cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DashboardOverviewRefreshQueue iniciada.");
        try
        {
            await RecoverOrphanedStatesAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                while (channel.Reader.TryRead(out var request))
                {
                    await ProcessRequestAsync(request, stoppingToken);
                }

                await EnqueueDueStatesAsync(stoppingToken);
                var signal = channel.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var tick = Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                await Task.WhenAny(signal, tick);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("DashboardOverviewRefreshQueue encerrada.");
    }

    private async Task EnqueueDueStatesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IMoodleSnapshotStore>();
        var courseScopeResolver = scope.ServiceProvider.GetRequiredService<DashboardCourseScopeResolver>();
        var now = DateTimeOffset.UtcNow;
        var states = await db.MoodleSyncStates.AsNoTracking()
            .Where(item => item.Dataset == MoodleSnapshotDatasets.DashboardPending &&
                           item.CourseId == string.Empty && item.NextSyncAt <= now &&
                           (item.Status != "running" || item.LeaseUntil == null || item.LeaseUntil <= now))
            .OrderBy(item => item.Priority)
            .Take(16)
            .ToArrayAsync(cancellationToken);
        foreach (var state in states)
        {
            var courses = await store.GetCoursesAsync(state.OwnerId, state.ConnectionAlias, cancellationToken);
            if (courses is null) continue;
            var scopedCourses = await courseScopeResolver.FilterAsync(
                state.OwnerId,
                state.ConnectionAlias,
                courses.Data,
                cancellationToken);
            if (scopedCourses.Count == 0)
            {
                await CompleteEmptyStateAsync(db, state.Id, now, cancellationToken);
                continue;
            }
            var request = new DashboardOverviewRefreshRequest(state.OwnerId, state.ClientId, state.ConnectionAlias, scopedCourses, ConnectionId: state.ConnectionId);
            var key = GetKey(request.OwnerId, request.ConnectionId ?? request.ConnectionAlias);
            if (queued.TryAdd(key, 0) && !channel.Writer.TryWrite(request)) queued.TryRemove(key, out _);
        }
    }

    private async Task RecoverOrphanedStatesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var orphanedStates = await db.MoodleSyncStates
            .Where(item => item.Dataset == MoodleSnapshotDatasets.DashboardPending &&
                           item.CourseId == string.Empty &&
                           item.Status == "running" &&
                           (item.LeaseUntil == null || item.LeaseUntil <= now) &&
                           (item.LastStartedAt == null || item.LastStartedAt < applicationStartedAt))
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
                "Atualizações do dashboard recuperadas após reinicialização. Count={Count}",
                recovered);
        }
    }

    private static Task<int> CompleteEmptyStateAsync(
        ConnectorDbContext db,
        Guid stateId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        db.MoodleSyncStates
            .Where(item => item.Id == stateId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "succeeded")
                .SetProperty(item => item.LastCompletedAt, now)
                .SetProperty(item => item.NextSyncAt, GetNextBrazilMidnight(now))
                .SetProperty(item => item.LastError, (string?)null)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.RecordsSynced, 0)
                .SetProperty(item => item.AttemptCount, 0)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);

    private async Task ProcessRequestAsync(DashboardOverviewRefreshRequest request, CancellationToken cancellationToken)
    {
        var key = GetKey(request.OwnerId, request.ConnectionId ?? request.ConnectionAlias);
        CancellationTokenSource? leaseHeartbeatCancellation = null;
        Task? leaseHeartbeat = null;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        Guid? runId = null;
        Guid? runItemId = null;
        Guid? stateId = null;
        var runStartedAt = DateTimeOffset.UtcNow;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var state = await TryClaimAsync(scope.ServiceProvider, request, cancellationToken);
            if (state is null) return;
            stateId = state.Id;

            leaseHeartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            leaseHeartbeat = RenewLeaseAsync(state.Id, leaseHeartbeatCancellation.Token);

            var executionContext = scope.ServiceProvider.GetRequiredService<IConnectorExecutionContext>();
            var connectionSelection = scope.ServiceProvider.GetRequiredService<IMoodleConnectionSelection>();
            var builder = scope.ServiceProvider.GetRequiredService<DashboardPendingSnapshotBuilder>();
            var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            var run = new MoodleSnapshotRunEntity
            {
                Id = Guid.NewGuid(),
                OwnerId = request.OwnerId,
                ConnectionId = request.ConnectionId ?? string.Empty,
                ConnectionAlias = request.ConnectionAlias,
                Trigger = "dashboard",
                WorkerId = WorkerId,
                SynchronizerVersion = typeof(DashboardOverviewRefreshQueue).Assembly.GetName().Version?.ToString() ?? "unknown",
                StartedAt = runStartedAt,
                CreatedAt = runStartedAt,
                ItemsTotal = 1
            };
            runId = run.Id;
            var runItem = new MoodleSnapshotRunItemEntity
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Dataset = MoodleSnapshotDatasets.DashboardPending,
                ResourceId = string.Empty,
                StartedAt = runStartedAt
            };
            runItemId = runItem.Id;
            db.MoodleSnapshotRuns.Add(run);
            db.MoodleSnapshotRunItems.Add(runItem);
            await db.SaveChangesAsync(cancellationToken);
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            executionContext.Enter(request.ClientId, request.OwnerId.ToString(), null);
            connectionSelection.Alias = request.ConnectionAlias;

            var snapshot = await builder.BuildAsync(request.OwnerId, request.Courses, cancellationToken);
            var store = scope.ServiceProvider.GetRequiredService<IMoodleSnapshotStore>();
            var generatedAt = snapshot.SnapshotGeneratedAt ?? DateTimeOffset.UtcNow;
            var snapshotComplete = snapshot.CoursesAnalyzed >= snapshot.CoursesInScope;
            var snapshotError = snapshotComplete
                ? null
                : $"A leitura de pendências ficou incompleta em {snapshot.CoursesInScope - snapshot.CoursesAnalyzed} curso(s).";
            await store.SaveAsync(
                request.OwnerId,
                request.ConnectionAlias,
                MoodleSnapshotDatasets.DashboardPending,
                string.Empty,
                snapshot,
                "hot",
                frozen: false,
                complete: snapshotComplete,
                snapshot.CoursesAnalyzed,
                generatedAt,
                cancellationToken,
                snapshotRunId: run.Id);

            var publishedSnapshot = await db.MoodleSnapshots
                .AsNoTracking()
                .Where(item => item.OwnerId == request.OwnerId &&
                               (item.ConnectionId == state.ConnectionId ||
                                item.ConnectionAlias == request.ConnectionAlias) &&
                               item.SnapshotType == MoodleSnapshotDatasets.DashboardPending &&
                               item.CourseId == string.Empty)
                .Select(item => new { item.PayloadHash, item.PayloadJson })
                .SingleOrDefaultAsync(cancellationToken);

            var persistedState = await db.MoodleSyncStates.SingleAsync(item => item.Id == state.Id, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            persistedState.Status = snapshotComplete ? "succeeded" : "partial";
            if (snapshotComplete)
            {
                persistedState.LastCompletedAt = completedAt;
            }
            persistedState.NextSyncAt = snapshotComplete
                ? GetNextBrazilMidnight(completedAt)
                : completedAt.AddSeconds(30);
            persistedState.LastError = snapshotError;
            persistedState.LeaseUntil = null;
            persistedState.RecordsSynced = snapshot.CoursesAnalyzed;
            persistedState.AttemptCount = 0;
            persistedState.UpdatedAt = DateTimeOffset.UtcNow;
            run.Status = snapshotComplete ? "succeeded" : "partial";
            run.FinishedAt = completedAt;
            run.ItemsSucceeded = snapshotComplete ? 1 : 0;
            run.ItemsFailed = snapshotComplete ? 0 : 1;
            run.RecordsSynced = snapshot.CoursesAnalyzed;
            run.DurationMs = Math.Max(0, (long)(completedAt - runStartedAt).TotalMilliseconds);
            run.Error = snapshotError;
            runItem.Status = snapshotComplete ? "succeeded" : "partial";
            runItem.FinishedAt = completedAt;
            runItem.DurationMs = Math.Max(0, (long)(completedAt - runStartedAt).TotalMilliseconds);
            runItem.RecordCount = snapshot.CoursesAnalyzed;
            runItem.PayloadHash = publishedSnapshot?.PayloadHash;
            runItem.PayloadSizeBytes = publishedSnapshot is null
                ? 0
                : System.Text.Encoding.UTF8.GetByteCount(publishedSnapshot.PayloadJson);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            await MarkRunTerminalAsync(runId, runItemId, "cancelled", runStartedAt, null, stateId);
            throw;
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            }
            await MarkFailedAsync(request, exception, runId, runItemId, runStartedAt, stateId, cancellationToken);
            logger.LogError(
                "Falha ao atualizar a visão geral do dashboard. ErrorCode={ErrorCode}",
                Moodle.Application.MoodleApi.MoodleErrorContract.NormalizeCode(
                    Moodle.Application.MoodleApi.MoodleErrorContract.Describe(exception).ErrorCode));
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
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
            queued.TryRemove(key, out _);
        }
    }

    private async Task RenewLeaseAsync(Guid stateId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
                await db.MoodleSyncStates
                    .Where(item => item.Id == stateId && item.Status == "running")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(options.LeaseMinutes))
                        .SetProperty(item => item.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            logger.LogWarning(
                "Não foi possível renovar o lease da atualização do dashboard. FailureReason={FailureReason}",
                "database_error");
        }
    }

    private async Task<MoodleSyncStateEntity?> TryClaimAsync(
        IServiceProvider services,
        DashboardOverviewRefreshRequest request,
        CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ConnectorDbContext>();
        var connectionId = string.IsNullOrWhiteSpace(request.ConnectionId)
            ? await MoodleConnectionIdentity.ResolveAsync(db, request.OwnerId, request.ClientId, request.ConnectionAlias, cancellationToken)
            : request.ConnectionId.Trim();
        var state = await db.MoodleSyncStates.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OwnerId == request.OwnerId &&
            (item.ConnectionId == connectionId ||
             (item.ConnectionId == string.Empty && item.ConnectionAlias == request.ConnectionAlias)) &&
            item.Dataset == MoodleSnapshotDatasets.DashboardPending && item.CourseId == string.Empty,
            cancellationToken);
        if (state is null || state.NextSyncAt is not { } next || next > DateTimeOffset.UtcNow) return null;
        var now = DateTimeOffset.UtcNow;
        if (MoodleSyncLeasePolicy.IsActive(state, now)) return null;
        var affected = await db.MoodleSyncStates.Where(item =>
                item.Id == state.Id &&
                (item.Status != "running" || item.LeaseUntil == null || item.LeaseUntil <= now) &&
                (item.LeaseUntil == null || item.LeaseUntil <= now) && item.NextSyncAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "running")
                .SetProperty(item => item.LastStartedAt, now)
                .SetProperty(item => item.LastAttemptAt, now)
                .SetProperty(item => item.LeaseUntil, now.AddMinutes(options.LeaseMinutes))
                .SetProperty(item => item.AttemptCount, state.AttemptCount + 1)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return affected == 1 ? state : null;
    }

    private async Task MarkFailedAsync(
        DashboardOverviewRefreshRequest request,
        Exception exception,
        Guid? runId,
        Guid? runItemId,
        DateTimeOffset runStartedAt,
        Guid? stateId,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var state = stateId is { } claimedStateId
            ? await db.MoodleSyncStates.SingleOrDefaultAsync(item => item.Id == claimedStateId, cancellationToken)
            : await db.MoodleSyncStates.SingleOrDefaultAsync(item =>
                item.OwnerId == request.OwnerId &&
                (item.ConnectionId == request.ConnectionId ||
                 (item.ConnectionId == string.Empty && item.ConnectionAlias == request.ConnectionAlias)) &&
                item.Dataset == MoodleSnapshotDatasets.DashboardPending && item.CourseId == string.Empty,
                cancellationToken);
        if (state is null) return;
        var seconds = Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, state.AttemptCount - 1))) * (0.75 + Random.Shared.NextDouble() * 0.5);
        state.Status = "failed";
        var descriptor = Moodle.Application.MoodleApi.MoodleErrorContract.Describe(exception);
        var safeError = descriptor.Message;
        if (exception is MoodleApiException moodleFailure && Guid.TryParse(moodleFailure.AuditId, out var callId))
        {
            safeError = $"{safeError} [callId={callId:N}]";
        }
        state.LastError = safeError.Length > 4000 ? safeError[..4000] : safeError;
        state.NextSyncAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        state.LeaseUntil = null;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await MarkRunTerminalAsync(runId, runItemId, "failed", runStartedAt, state.LastError, stateId, db);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkRunTerminalAsync(
        Guid? runId,
        Guid? runItemId,
        string status,
        DateTimeOffset runStartedAt,
        string? error,
        Guid? stateId = null,
        ConnectorDbContext? existingDb = null)
    {
        if (runId is null && runItemId is null) return;
        try
        {
            using var scope = existingDb is null ? scopeFactory.CreateScope() : null;
            var db = existingDb ?? scope!.ServiceProvider.GetRequiredService<ConnectorDbContext>();
            var now = DateTimeOffset.UtcNow;
            if (stateId is { } persistedStateId)
            {
                var state = await db.MoodleSyncStates.SingleOrDefaultAsync(item => item.Id == persistedStateId);
                if (state is not null)
                {
                    state.Status = status;
                    state.LeaseUntil = null;
                    state.NextSyncAt = status == "cancelled" ? now : state.NextSyncAt;
                    state.UpdatedAt = now;
                }
            }
            if (runId is { } persistedRunId)
            {
                var run = await db.MoodleSnapshotRuns.SingleOrDefaultAsync(item => item.Id == persistedRunId);
                if (run is not null)
                {
                    run.Status = status;
                    run.FinishedAt = now;
                    run.DurationMs = Math.Max(0, (long)(now - runStartedAt).TotalMilliseconds);
                    run.Error = error;
                    run.ItemsSucceeded = status == "succeeded" ? 1 : 0;
                    run.ItemsFailed = status is "failed" or "cancelled" ? 1 : 0;
                }
            }
            if (runItemId is { } persistedRunItemId)
            {
                var item = await db.MoodleSnapshotRunItems.SingleOrDefaultAsync(item => item.Id == persistedRunItemId);
                if (item is not null)
                {
                    item.Status = status;
                    item.FinishedAt = now;
                    item.DurationMs = Math.Max(0, (long)(now - item.StartedAt).TotalMilliseconds);
                    item.Error = error;
                }
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            logger.LogWarning(
                "Não foi possível persistir o estado final do run de dashboard. FailureReason={FailureReason}",
                "database_error");
        }
    }

    private static string GetKey(Guid ownerId, string connectionRef) =>
        $"{ownerId}:{connectionRef}";

    private static bool IsSyncStateUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        (postgres.ConstraintName is "IX_moodle_sync_states_scope" or "IX_moodle_sync_states_connection_scope");

    private static DateTimeOffset GetNextBrazilMidnight(DateTimeOffset now)
    {
        var timeZone = ResolveBrazilTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var nextLocalMidnight = localNow.Date.AddDays(1);
        return new DateTimeOffset(nextLocalMidnight, timeZone.GetUtcOffset(nextLocalMidnight)).ToUniversalTime();
    }

    private static TimeZoneInfo ResolveBrazilTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
    }
}

internal sealed class DashboardPendingSnapshotBuilder(
    IServiceScopeFactory scopeFactory,
    ConnectorDbContext dbContext,
    IConnectorExecutionContext executionContext,
    IMoodleConnectionSelection connectionSelection)
{
    public async Task<AppDashboardPendingMetricDto> BuildAsync(
        Guid ownerId,
        IReadOnlyList<CourseSummary> courses,
        CancellationToken cancellationToken)
    {
        var clientId = executionContext.ClientId
            ?? throw new InvalidOperationException("O contexto de execução do dashboard não possui ClientId.");
        var subject = executionContext.Subject ?? ownerId.ToString();
        var pendingResults = await ReadPendingAsync(
            ownerId,
            courses,
            scopeFactory,
            clientId,
            subject,
            executionContext.Email,
            executionContext.Scopes,
            connectionSelection.Alias,
            ownerId.ToString(),
            cancellationToken);
        var pendingRows = pendingResults.SelectMany(item => item.Rows).ToArray();
        var gradingRows = pendingResults.SelectMany(item => item.GradingRows).ToArray();
        var pendingItems = pendingResults.SelectMany(item => item.PendingItems).ToArray();
        var pendingStudentIds = pendingResults
            .SelectMany(item => item.Rows.Select(row => row.StudentId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overdueStudentIds = pendingResults
            .SelectMany(item => item.Rows.Where(row => row.Level == "risk").Select(row => row.StudentId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = pendingResults
            .SelectMany(item => item.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var coursesAnalyzed = pendingResults.Count(item => item.IsComplete);
        if (coursesAnalyzed < courses.Count)
        {
            warnings.Insert(0,
                $"A leitura de pendências ficou incompleta em {courses.Count - coursesAnalyzed} de {courses.Count} curso(s). " +
                "Os dados exibidos não representam todo o escopo.");
        }
        var courseSummaries = pendingResults
            .Select(item => new AppDashboardCoursePendingSummaryDto(
                item.CourseId,
                item.CourseName,
                item.CorrectionActivities,
                item.GradingRows.Count,
                item.PendingActivities,
                item.Rows.Count,
                item.GradingRows.Select(row => row.StudentId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                item.Rows.Select(row => row.StudentId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                item.Rows.Count(row => row.Level == "risk"),
                item.Warnings.Any(warning => warning.Contains("limitad", StringComparison.OrdinalIgnoreCase)),
                item.Warnings.FirstOrDefault()))
            .Where(item => item.PendingCorrectionActivities > 0 || item.PendingSubmissionActivities > 0)
            .OrderByDescending(item => item.PendingCorrectionSubmissions + item.PendingSubmissions)
            .ThenBy(item => item.CourseName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var summary = new AppDashboardSummaryDto(
            courses.Count,
            pendingRows.Length,
            courseSummaries.Sum(item => item.PendingCorrectionSubmissions),
            overdueStudentIds.Count,
            pendingStudentIds.Count)
        {
            ActivitiesToReview = courseSummaries.Sum(item => item.PendingCorrectionSubmissions),
            PendingSubmissionAssignments = pendingRows.Length,
            PendingCorrectionAssignments = courseSummaries.Sum(item => item.PendingCorrectionSubmissions),
        };

        return new AppDashboardPendingMetricDto(
            summary,
            pendingResults.SelectMany(item => item.PriorityRows).Concat(gradingRows).Take(AppDashboardBudget.MaxPriorities).ToArray(),
            gradingRows.Take(AppDashboardBudget.MaxCorrectionItems).ToArray(),
            courseSummaries,
            await ReadTodayItemsAsync(ownerId, cancellationToken),
            warnings.ToArray())
        {
            IsRefreshing = false,
            CoursesInScope = courses.Count,
            CoursesAnalyzed = coursesAnalyzed,
            SnapshotGeneratedAt = DateTimeOffset.UtcNow,
            PendingItems = pendingItems,
        };
    }

    public async Task<AppDashboardPendingMetricDto> CreateRefreshingAsync(
        Guid ownerId,
        int coursesInScope,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return new AppDashboardPendingMetricDto(
            new AppDashboardSummaryDto(coursesInScope, 0, 0, 0, 0),
            [],
            [],
            [],
            await ReadTodayItemsAsync(ownerId, cancellationToken),
            ["A visão geral das atividades pendentes está sendo atualizada no Moodle."])
        {
            IsRefreshing = true,
            CoursesInScope = coursesInScope,
            CoursesAnalyzed = 0,
            SnapshotGeneratedAt = null,
        };
    }

    public async Task<AppDashboardPendingMetricDto> CreateEmptyAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        return new AppDashboardPendingMetricDto(
            new AppDashboardSummaryDto(0, 0, 0, 0, 0),
            [],
            [],
            [],
            await ReadTodayItemsAsync(ownerId, cancellationToken),
            [])
        {
            IsRefreshing = false,
            CoursesInScope = 0,
            CoursesAnalyzed = 0,
            SnapshotGeneratedAt = null,
        };
    }

    private static async Task<IReadOnlyList<DashboardPendingRead>> ReadPendingAsync(
        Guid ownerId,
        IReadOnlyList<CourseSummary> courses,
        IServiceScopeFactory scopeFactory,
        string clientId,
        string subject,
        string? email,
        IReadOnlyCollection<string> scopes,
        string? connectionAlias,
        string userExternalId,
        CancellationToken cancellationToken)
    {
        // Reuse only fresh local activity and participant snapshots. Delivery
        // state is always read live by the per-course background worker.
        DashboardPendingSnapshotBatch? snapshotBatch = null;
        Exception? snapshotBatchReadFailure = null;
        if (!string.IsNullOrWhiteSpace(connectionAlias))
        {
            try
            {
                await using var snapshotScope = scopeFactory.CreateAsyncScope();
                var snapshotExecutionContext = snapshotScope.ServiceProvider.GetRequiredService<IConnectorExecutionContext>();
                snapshotExecutionContext.Enter(clientId, subject, email, scopes);
                var snapshotConnectionSelection = snapshotScope.ServiceProvider.GetRequiredService<IMoodleConnectionSelection>();
                snapshotConnectionSelection.Alias = connectionAlias;
                var snapshotStore = snapshotScope.ServiceProvider.GetRequiredService<IMoodleSnapshotStore>();
                snapshotBatch = await DashboardPendingSnapshotBatchReader.ReadAsync(
                    snapshotStore,
                    ownerId,
                    connectionAlias,
                    courses.Select(course => course.CourseId).ToArray(),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                snapshotBatchReadFailure = exception;
            }
        }

        using var limiter = new SemaphoreSlim(AppDashboardBudget.PendingCourseConcurrency);
        var tasks = courses.Select(async course =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                await using var courseScope = scopeFactory.CreateAsyncScope();
                var courseExecutionContext = courseScope.ServiceProvider.GetRequiredService<IConnectorExecutionContext>();
                courseExecutionContext.Enter(clientId, subject, email, scopes);
                var courseConnectionSelection = courseScope.ServiceProvider.GetRequiredService<IMoodleConnectionSelection>();
                courseConnectionSelection.Alias = connectionAlias;
                var courseMediator = courseScope.ServiceProvider.GetRequiredService<IMediator>();
                CourseParticipantsPage? prefetchedParticipants = null;
                if (!string.IsNullOrWhiteSpace(connectionAlias))
                {
                    if (snapshotBatchReadFailure is not null)
                    {
                        throw new InvalidOperationException("A leitura em lote dos snapshots do dashboard falhou.", snapshotBatchReadFailure);
                    }

                    var batch = snapshotBatch!;
                    batch.Students.TryGetValue(course.CourseId.Trim(), out var participantSnapshot);
                    if (participantSnapshot is { IsComplete: true, IsStale: false })
                    {
                        prefetchedParticipants = participantSnapshot.Data;
                    }

                    var snapshotsReady = prefetchedParticipants is not null;
                    if (!snapshotsReady)
                    {
                        var snapshotQueue = courseScope.ServiceProvider.GetRequiredService<IMoodleSnapshotSyncQueue>();
                        if (prefetchedParticipants is null)
                        {
                            await snapshotQueue.EnqueueAsync(new MoodleSnapshotSyncRequest(
                                ownerId,
                                clientId,
                                connectionAlias!,
                                userExternalId,
                                Dataset: MoodleSnapshotDatasets.Students,
                                CourseId: course.CourseId,
                                Priority: 10,
                                Force: participantSnapshot is { IsComplete: true, IsStale: true }), cancellationToken);
                        }
                        return new DashboardPendingRead(
                            course.CourseId,
                            course.FullName,
                            [],
                            [],
                            [],
                            [],
                            0,
                            0,
                            [$"[{course.CourseId}] {course.FullName}: os dados locais deste curso estão sendo preparados."],
                            false);
                    }

                }
                else
                {
                    return new DashboardPendingRead(
                        course.CourseId,
                        course.FullName,
                        [],
                        [],
                        [],
                        [],
                        0,
                        0,
                        [$"[{course.CourseId}] {course.FullName}: a conexão Moodle não está disponível para preparar o snapshot."],
                        false);
                }
                var pending = await courseMediator.Send(new GetStudentsWithPendingSubmissionsQuery(
                    course.CourseId,
                    DueDaysAhead: 0,
                    MaxStudentsToAnalyze: AppDashboardBudget.MaxParticipantsRead,
                    IncludeAwaitingGrading: true,
                    MaxAssignmentsToAnalyze: AppDashboardBudget.MaxAssignmentsRead,
                    PrefetchedParticipants: prefetchedParticipants), cancellationToken);

                var rows = pending.Students
                    .SelectMany(student => student.PendingAssignments.Select(activity => new DashboardPendingRow(
                        student.StudentId,
                        activity.IsOverdue ? "risk" : "attention")))
                    .ToArray();
                var pendingItems = pending.Students
                    .SelectMany(student => student.PendingAssignments.Select(activity => new AppDashboardPendingItemDto(
                        course.CourseId,
                        student.StudentId,
                        student.FullName,
                        student.LastCourseAccessAt,
                        activity.AssignmentId,
                        activity.AssignmentName,
                        activity.DueDate,
                        activity.IsOverdue)))
                    .ToArray();
                var priorityRows = pending.Students
                    .SelectMany(student => student.PendingAssignments.Select(activity => new AppDashboardPriorityDto(
                        $"{course.CourseId}:{student.StudentId}:{activity.AssignmentId}",
                        "Entrega pendente",
                        $"{student.FullName} · {activity.AssignmentName}",
                        activity.IsOverdue ? "risk" : "attention",
                        course.CourseId,
                        student.StudentId)))
                    .ToArray();
                var gradingRows = pending.AwaitingGrading
                    .Select(item => new AppDashboardPriorityDto(
                        $"{course.CourseId}:{item.StudentId}:{item.AssignmentId}:grading",
                        "Atividade para corrigir",
                        $"{item.StudentName} · {item.AssignmentName}",
                        "attention",
                        course.CourseId,
                        item.StudentId))
                    .ToArray();
                var pendingActivities = pending.Students
                    .SelectMany(student => student.PendingAssignments)
                    .Select(activity => activity.AssignmentId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var correctionActivities = pending.AwaitingGrading
                    .Select(item => item.AssignmentId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                var warnings = pending.Warning is null
                    ? Array.Empty<string>()
                    : [$"[{course.CourseId}] {course.FullName}: {pending.Warning}"];
                return new DashboardPendingRead(
                    course.CourseId,
                    course.FullName,
                    rows,
                    pendingItems,
                    priorityRows,
                    gradingRows,
                    pendingActivities,
                    correctionActivities,
                    warnings,
                    pending.IsComplete);
            }
            catch
            {
                return new DashboardPendingRead(
                    course.CourseId,
                    course.FullName,
                    [],
                    [],
                    [],
                    [],
                    0,
                    0,
                    [$"Não foi possível carregar as pendências do curso {course.FullName} (courseId={course.CourseId})."],
                    false);
            }
            finally
            {
                limiter.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    private async Task<IReadOnlyList<AppDashboardTodayItemDto>> ReadTodayItemsAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        var todayStart = GetBrazilTodayStart(DateTimeOffset.UtcNow);
        var todayEnd = todayStart.AddDays(1);
        var tasks = await dbContext.Tasks.AsNoTracking()
            .Where(item => item.OwnerId == ownerId && item.Status != "done" && item.DueAt >= todayStart && item.DueAt < todayEnd)
            .OrderBy(item => item.DueAt)
            .Take(AppDashboardBudget.MaxPriorities)
            .Select(item => new AppDashboardTodayItemDto($"task:{item.Id}", "task", item.Title, "Tarefa", item.DueAt))
            .ToArrayAsync(cancellationToken);
        var events = await dbContext.CalendarEvents.AsNoTracking()
            .Where(item => item.OwnerId == ownerId && item.StartAt >= todayStart && item.StartAt < todayEnd)
            .OrderBy(item => item.StartAt)
            .Take(AppDashboardBudget.MaxPriorities)
            .Select(item => new AppDashboardTodayItemDto($"event:{item.Id}", "event", item.Title, "Evento", item.StartAt))
            .ToArrayAsync(cancellationToken);

        return tasks.Concat(events)
            .OrderBy(item => item.StartsAt)
            .Take(AppDashboardBudget.MaxPriorities)
            .ToArray();
    }

    private static DateTimeOffset GetBrazilTodayStart(DateTimeOffset value)
    {
        var brazil = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "E. South America Standard Time" : "America/Sao_Paulo");
        var local = TimeZoneInfo.ConvertTime(value, brazil);
        var date = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(date, brazil.GetUtcOffset(date)).ToUniversalTime();
    }

    private sealed record DashboardPendingRead(
        string CourseId,
        string CourseName,
        IReadOnlyList<DashboardPendingRow> Rows,
        IReadOnlyList<AppDashboardPendingItemDto> PendingItems,
        IReadOnlyList<AppDashboardPriorityDto> PriorityRows,
        IReadOnlyList<AppDashboardPriorityDto> GradingRows,
        int PendingActivities,
        int CorrectionActivities,
        IReadOnlyList<string> Warnings,
        bool IsComplete);

    private sealed record DashboardPendingRow(string StudentId, string Level);
}
