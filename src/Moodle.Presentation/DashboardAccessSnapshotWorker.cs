using Microsoft.EntityFrameworkCore;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;

namespace Moodle.Presentation;

/// <summary>
/// Keeps the daily access history alive even when nobody opens the dashboard.
/// The durable daily row is updated repeatedly during the day and always keeps
/// the observation with the newest GeneratedAt for that Brazil calendar date.
/// </summary>
internal sealed class DashboardAccessSnapshotWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DashboardAccessSnapshotWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DashboardAccessSnapshotWorker iniciada.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CaptureAllConnectionsAsync(stoppingToken);
                await Task.Delay(CaptureInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            logger.LogInformation("DashboardAccessSnapshotWorker encerrada.");
        }
    }

    private async Task CaptureAllConnectionsAsync(CancellationToken cancellationToken)
    {
        using var discoveryScope = scopeFactory.CreateScope();
        var db = discoveryScope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var targets = await db.UserAccounts
            .AsNoTracking()
            .Where(account => account.ConnectorClientId != null)
            .Join(
                db.ConnectorClients.AsNoTracking().Where(connection => connection.IsActive),
                account => account.ConnectorClientId!,
                connection => connection.ClientId,
                (account, connection) => new DashboardAccessTarget(
                    account.Id,
                    connection.ClientId,
                    connection.MoodleAlias))
            .ToArrayAsync(cancellationToken);

        foreach (var target in targets)
        {
            try
            {
                await using var targetScope = scopeFactory.CreateAsyncScope();
                var executionContext = targetScope.ServiceProvider.GetRequiredService<IConnectorExecutionContext>();
                executionContext.Enter(target.ClientId, target.OwnerId.ToString(), null);
                var connectionSelection = targetScope.ServiceProvider.GetRequiredService<IMoodleConnectionSelection>();
                connectionSelection.Alias = target.ConnectionAlias;

                var snapshotStore = targetScope.ServiceProvider.GetRequiredService<IMoodleSnapshotStore>();
                var existingSnapshot = await snapshotStore.GetAsync<DashboardAccessRead>(
                    target.OwnerId,
                    target.ConnectionAlias,
                    MoodleSnapshotDatasets.DashboardAccess,
                    cancellationToken: cancellationToken);
                if (existingSnapshot is not null && !existingSnapshot.IsStale)
                {
                    continue;
                }

                var resolver = targetScope.ServiceProvider.GetRequiredService<DashboardCourseScopeResolver>();
                var courses = await resolver.ResolveAsync(target.OwnerId, target.ConnectionAlias, cancellationToken);
                if (courses.Count == 0)
                {
                    logger.LogInformation(
                        "Daily dashboard access snapshot skipped because My Courses is empty.");
                    continue;
                }
                var accessService = targetScope.ServiceProvider.GetRequiredService<DashboardAccessSnapshotService>();
                var access = await accessService.ReadAsync(courses, cancellationToken);
                var generatedAt = DateTimeOffset.UtcNow;
                await accessService.PersistAsync(
                    target.OwnerId,
                    target.ConnectionAlias,
                    access,
                    generatedAt,
                    courses.Count,
                    persistCurrentSnapshot: true,
                    cancellationToken);

                logger.LogInformation(
                    "Daily dashboard access snapshot captured. Courses={Courses} Students={Students}",
                    courses.Count,
                    access.TotalStudents);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var errorCode = MoodleErrorContract.Describe(exception).ErrorCode;
                logger.LogWarning(
                    "Could not capture daily dashboard access snapshot. ErrorCode={ErrorCode}",
                    MoodleErrorContract.NormalizeCode(errorCode));
            }
        }
    }

    private sealed record DashboardAccessTarget(
        Guid OwnerId,
        string ClientId,
        string ConnectionAlias);
}
