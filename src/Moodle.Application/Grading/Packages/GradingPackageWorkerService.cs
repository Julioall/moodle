using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;

namespace Moodle.Application.Grading.Packages;

/// <summary>
/// Despacha preparação em background. O canal é apenas uma aceleração local;
/// os pacotes em Queued continuam visíveis ao polling após reinício ou em
/// outra réplica.
/// </summary>
public sealed class GradingPackageWorkerService(
    GradingPackagePreparationChannel channel,
    IServiceScopeFactory scopeFactory,
    IOptions<GradingLimitsOptions> limits,
    ILogger<GradingPackageWorkerService> logger,
    IMoodleAdmissionWorkScope? admissionWorkScope = null) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, byte> activeWorkItems = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            RunConsumersAsync(stoppingToken),
            PollDurablePackagesAsync(stoppingToken));

    private Task RunConsumersAsync(CancellationToken cancellationToken)
    {
        var count = Math.Clamp(limits.Value.GradingPackageWorkerConcurrency, 1, 8);
        return Task.WhenAll(Enumerable.Range(0, count).Select(_ => ConsumeAsync(cancellationToken)));
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        while (await channel.WaitToReadAsync(cancellationToken))
        {
            while (channel.TryRead(out var workItem))
            {
                var activeKey = workItem.PackageId ?? workItem.BatchJobId;
                if (!activeWorkItems.TryAdd(activeKey, 0))
                {
                    continue;
                }

                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IGradingReviewRepository>();
                    var batch = await repository.GetBatchAsync(workItem.BatchJobId, cancellationToken);
                    if (batch is null)
                    {
                        logger.LogWarning("Lote {BatchJobId} nao encontrado ao preparar pacote.", workItem.BatchJobId);
                        continue;
                    }

                    var executionContext = scope.ServiceProvider.GetService<IConnectorExecutionContext>();
                    var connectionSelection = scope.ServiceProvider.GetService<IMoodleConnectionSelection>();
                    var backgroundConnectionRestorer = scope.ServiceProvider
                        .GetService<IMoodleBackgroundConnectionContextRestorer>();
                    var enteredExecutionContext = false;
                    var worker = scope.ServiceProvider.GetRequiredService<GradingPackageWorker>();
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(batch.ConnectorClientId) && executionContext is not null)
                        {
                            executionContext.Enter(batch.ConnectorClientId, batch.CreatedBySubject, null);
                            enteredExecutionContext = true;
                            if (connectionSelection is not null)
                            {
                                connectionSelection.Alias = batch.ConnectionAlias;
                            }
                        }
                        else if (executionContext is not null &&
                                 backgroundConnectionRestorer is not null &&
                                 !string.IsNullOrWhiteSpace(batch.MoodleConnectionId))
                        {
                            enteredExecutionContext = await backgroundConnectionRestorer.TryRestoreAsync(
                                batch.MoodleConnectionId,
                                batch.CreatedBySubject,
                                cancellationToken);
                        }

                        var admissionOwnerId = ResolveAdmissionOwnerId(
                            executionContext?.Subject ?? batch.CreatedBySubject,
                            executionContext?.ClientId ?? batch.ConnectorClientId);
                        using var admissionScope = enteredExecutionContext &&
                                                   admissionWorkScope is not null &&
                                                   admissionOwnerId.Length is > 0 and <= 128
                            ? admissionWorkScope.Begin(
                                MoodleAdmissionOrigin.Grading,
                                admissionOwnerId,
                                MoodleAdmissionPriority.Background)
                            : null;

                        if (workItem.PackageId is Guid packageId)
                        {
                            await worker.PrepareForBackgroundAsync(
                                workItem.BatchJobId,
                                packageId,
                                cancellationToken);
                        }
                        else
                        {
                            // Legacy batch-wide work has no persisted selection
                            // and still needs the full-batch reconstruction path.
                            await worker.PrepareAsync(workItem.BatchJobId, null, cancellationToken);
                        }
                    }
                    finally
                    {
                        if (enteredExecutionContext)
                        {
                            if (connectionSelection is not null)
                            {
                                connectionSelection.Alias = null;
                            }

                            executionContext!.Clear();
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Falha ao preparar pacotes de correcao do lote {BatchJobId}, pacote {PackageId}. ErrorCode={ErrorCode}",
                        workItem.BatchJobId,
                        workItem.PackageId,
                        MoodleErrorContract.Describe(exception).ErrorCode);
                }
                finally
                {
                    activeWorkItems.TryRemove(activeKey, out _);
                }
            }
        }
    }

    private async Task PollDurablePackagesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(limits.Value.GradingPackagePollSeconds, 1, 300)));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<IGradingPackageStore>();
                    await store.RecoverExpiredClaimsAsync(
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                    var packages = await store.ListDueAsync(
                        DateTimeOffset.UtcNow,
                        Math.Clamp(limits.Value.GradingPackageWorkerConcurrency * 2, 1, 32),
                        cancellationToken);
                    foreach (var package in packages)
                    {
                        if (package.BatchJobId is Guid batchJobId)
                        {
                            channel.TryEnqueue(batchJobId, package.Id);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Falha transitória ao consultar pacotes de correcao pendentes; o polling continuara. ErrorCode={ErrorCode}",
                        MoodleErrorContract.Describe(exception).ErrorCode);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string ResolveAdmissionOwnerId(string? subject, string? clientId)
    {
        if (Guid.TryParse(subject, out var ownerId)) return ownerId.ToString("N");
        return !string.IsNullOrWhiteSpace(clientId) ? clientId : subject ?? string.Empty;
    }
}
