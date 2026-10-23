using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Background worker que consome lotes da GradingBatchChannel e processa
/// os itens pendentes de forma assíncrona, sem travar o chat MCP.
/// Ao iniciar, retoma lotes que ficaram em Processing após queda do processo.
/// </summary>
public sealed class GradingBatchWorkerService(
    GradingBatchChannel channel,
    IServiceScopeFactory scopeFactory,
    IOptions<GradingLimitsOptions> limits,
    ILogger<GradingBatchWorkerService> logger) : BackgroundService
{
    private const int ItemClaimWindowSize = 16;
    private const int CheckpointInterval = 25;
    private readonly ConcurrentDictionary<Guid, byte> activeBatches = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> connectionGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly AdaptiveConcurrencyGate concurrencyGate = new(initialTarget: 1);

    private static readonly string WorkerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GradingBatchWorkerService iniciado.");
        var maxConcurrency = ResolveMaximumConcurrency();
        var initialConcurrency = ResolveInitialConcurrency(maxConcurrency);
        concurrencyGate.SetTarget(initialConcurrency);
        logger.LogInformation(
            "Pool adaptativo de correção: inicial={InitialConcurrency}, minimo={MinimumConcurrency}, maximo={MaximumConcurrency}, cpuAlvo={CpuTarget}%, memoriaAlvo={MemoryTarget}%.",
            initialConcurrency,
            ResolveMinimumConcurrency(),
            maxConcurrency,
            ResolveCpuTargetPercent(),
            ResolveMemoryTargetPercent());

        if (!IsDurableJobStoreAvailable())
        {
            // Compatibilidade para hosts que ainda não registraram o job store.
            // Inicie os consumidores antes do catch-up para que uma fila
            // grande de lotes Processing não bloqueie o startup no buffer.
            await Task.WhenAll(
                ProcessChannelConsumersAsync(stoppingToken),
                ResumeInProgressBatchesAsync(stoppingToken),
                AdaptConcurrencyAsync(stoppingToken));
            return;
        }

        try
        {
            await RecoverExpiredBatchesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            logger.LogWarning("Não foi possível recuperar leases expirados no startup; o polling tentará novamente. FailureReason={FailureReason}", "worker_error");
        }
        // O polling durável retoma apenas lotes elegíveis (com itens Pending)
        // depois que os consumidores já estão ativos. Reenfileirar todos os
        // lotes Processing antes de iniciar os consumidores pode bloquear em
        // um canal cheio e impedir o próprio polling de iniciar.

        try
        {
            await Task.WhenAll(
                ProcessChannelConsumersAsync(stoppingToken),
                PollDurableBatchesAsync(stoppingToken),
                AdaptConcurrencyAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown normal do host.
        }

        logger.LogInformation("GradingBatchWorkerService encerrado.");
    }

    private Task ProcessChannelConsumersAsync(CancellationToken cancellationToken)
    {
        var consumerCount = limits.Value.AdaptiveBatchWorkerConcurrency
            ? ResolveMaximumConcurrency()
            : ResolveInitialConcurrency(ResolveMaximumConcurrency());
        return Task.WhenAll(Enumerable.Range(0, consumerCount)
            .Select(_ => ProcessChannelAsync(cancellationToken)));
    }

    private async Task ProcessChannelAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Acquire the adaptive slot before removing work from the
                // channel. This prevents low-demand operation from holding
                // durable leases for batches waiting behind the connection
                // gate.
                await concurrencyGate.EnterAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!channel.TryRead(out var workItem))
            {
                concurrencyGate.Exit();
                try
                {
                    if (!await channel.WaitToReadAsync(cancellationToken))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            try
            {
                await ProcessBatchAsync(workItem.BatchId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Falha fatal ao processar lote do worker. BatchId={BatchId} ErrorCode={ErrorCode}",
                    workItem.BatchId,
                    MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(ex).ErrorCode));
            }
            finally
            {
                concurrencyGate.Exit();
            }
        }
    }

    private async Task PollDurableBatchesAsync(CancellationToken cancellationToken)
    {
        var pollSeconds = Math.Clamp(limits.Value.DurableBatchPollSeconds, 1, 300);
        var configuredClaimSize = Math.Clamp(limits.Value.DurableBatchClaimSize, 1, 100);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var jobStore = scope.ServiceProvider.GetRequiredService<IGradingBatchJobStore>();
                    var now = DateTimeOffset.UtcNow;
                    // Claim only as many durable jobs as the current pool can
                    // start. Remaining Pending jobs stay visible to the next
                    // cycle and are not stranded behind a long lease.
                    var claimSize = Math.Clamp(
                        Math.Min(configuredClaimSize, concurrencyGate.Target),
                        1,
                        100);
                    var claims = await jobStore.ClaimDueBatchesAsync(
                        WorkerId,
                        now,
                        LeaseDuration,
                        claimSize,
                        cancellationToken);

                    foreach (var claim in claims)
                    {
                        if (channel.TryEnqueue(new GradingBatchWorkItem(claim.BatchId, now, WorkerId)))
                        {
                            continue;
                        }

                        // A fila local é apenas uma aceleração. Se ela estiver
                        // cheia, devolvemos o lease imediatamente para que o
                        // próximo ciclo (ou outra réplica) possa tentar de novo.
                        await jobStore.ReleaseBatchLeaseAsync(
                            claim.BatchId,
                            WorkerId,
                            now,
                            errorCode: null,
                            nextAttemptAt: now,
                            cancellationToken);
                        logger.LogDebug(
                            "Canal local cheio; lease do lote {BatchId} devolvido ao polling durável.",
                            claim.BatchId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    logger.LogWarning("Falha no polling durável de lotes de correção; nova tentativa no próximo ciclo. FailureReason={FailureReason}", "worker_error");
                }

                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            logger.LogWarning("Falha no polling durável de lotes de correção assistida. FailureReason={FailureReason}", "worker_error");
        }
    }

    private async Task RecoverExpiredBatchesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var jobStore = scope.ServiceProvider.GetRequiredService<IGradingBatchJobStore>();
        var now = DateTimeOffset.UtcNow;
        var recovered = await jobStore.RecoverExpiredBatchLeasesAsync(
            now,
            cancellationToken);
        var recoveredItems = await jobStore.RecoverExpiredItemLeasesAsync(
            now,
            cancellationToken);
        if (recovered > 0)
        {
            logger.LogInformation("Recuperados {Count} lease(s) expirado(s) de lotes de correção.", recovered);
        }

        if (recoveredItems > 0)
        {
            logger.LogInformation("Recuperados {Count} lease(s) expirado(s) de itens de correção.", recoveredItems);
        }
    }

    private bool IsDurableJobStoreAvailable()
    {
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<IGradingBatchJobStore>() is not null;
    }

    private async Task ResumeInProgressBatchesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IGradingReviewRepository>();
            var inProgressBatches = await repository.ListBatchesByStatusAsync(
                GradingBatchStatus.Processing,
                cancellationToken);

            if (inProgressBatches.Count == 0)
            {
                return;
            }

            logger.LogInformation(
                "Retomando {Count} lote(s) em Processing encontrado(s) no startup.",
                inProgressBatches.Count);

            foreach (var batch in inProgressBatches)
            {
                await channel.EnqueueAsync(
                    new GradingBatchWorkItem(batch.Id, DateTimeOffset.UtcNow),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown durante catch-up — normal.
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Falha ao tentar retomar lotes em Processing no startup. ErrorCode={ErrorCode}",
                MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(ex).ErrorCode));
        }
    }

    private async Task ProcessBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        // O channel pode conter o mesmo lote mais de uma vez (por polling e por
        // enqueue legado). O lease durável protege réplicas distintas; este
        // guard protege também reentrância concorrente dentro do mesmo processo.
        if (!activeBatches.TryAdd(batchId, 0))
        {
            logger.LogDebug("Lote {BatchId} já está em processamento neste worker; duplicata ignorada.", batchId);
            return;
        }

        try
        {
            await ProcessBatchCoreAsync(batchId, cancellationToken);
        }
        finally
        {
            activeBatches.TryRemove(batchId, out _);
        }
    }

    private async Task ProcessBatchCoreAsync(Guid batchId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IGradingReviewRepository>();
        var jobStore = scope.ServiceProvider.GetService<IGradingBatchJobStore>();
        var processor = scope.ServiceProvider.GetRequiredService<GradingItemProcessor>();
        var ingestionService = scope.ServiceProvider.GetRequiredService<IGradingArtifactIngestionService>();
        var executionContext = scope.ServiceProvider.GetService<IConnectorExecutionContext>();
        var connectionSelection = scope.ServiceProvider.GetService<IMoodleConnectionSelection>();
        var maxItems = limits.Value.MaxBatchItems;

        if (jobStore is not null && await jobStore.TryClaimBatchAsync(
                batchId,
                WorkerId,
                DateTimeOffset.UtcNow,
                LeaseDuration,
                cancellationToken) is null)
        {
            logger.LogDebug("Lote {BatchId} já possui claim ativo ou não está pronto; ignorado.", batchId);
            return;
        }

        var batch = await repository.GetBatchAsync(batchId, cancellationToken);
        if (batch is null)
        {
            logger.LogWarning("Lote {BatchId} nao encontrado no worker; ignorado.", batchId);
            return;
        }

        var connectionKey = ResolveConnectionKey(batch);
        var connectionGate = connectionGates.GetOrAdd(
            connectionKey,
            _ => new SemaphoreSlim(
                Math.Clamp(limits.Value.BatchWorkerPerConnectionConcurrency, 1, 16),
                Math.Clamp(limits.Value.BatchWorkerPerConnectionConcurrency, 1, 16)));
        await connectionGate.WaitAsync(cancellationToken);

        var enteredExecutionContext = false;
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
        }
        catch
        {
            if (enteredExecutionContext)
            {
                if (connectionSelection is not null)
                {
                    connectionSelection.Alias = null;
                }
                executionContext!.Clear();
            }
            connectionGate.Release();
            throw;
        }

        try
        {
            if (batch.Status is GradingBatchStatus.Cancelled or GradingBatchStatus.Completed)
            {
                if (jobStore is not null)
                {
                    await jobStore.ReleaseBatchLeaseAsync(
                        batchId,
                        WorkerId,
                        DateTimeOffset.UtcNow,
                        errorCode: null,
                        nextAttemptAt: null,
                        cancellationToken);
                }

                logger.LogDebug(
                    "Lote {BatchId} em status {Status}; processamento ignorado.",
                    batchId,
                    batch.Status);
                return;
            }

            var items = await GradingItemProcessor.LoadAllBatchItemsAsync(
                repository,
                batchId,
                cancellationToken,
                maxItems);
            var pendingItems = items.Where(item => item.Status == GradingItemStatus.Pending).ToArray();

            // Artifact references are immutable for a batch item in the
            // normal path. Let ingestion hydrate them once for the entire
            // child batch instead of issuing two artifact SELECTs per item.
            await ingestionService.PrepareBatchAsync(
                batch,
                pendingItems.Select(item => item.Id).ToArray(),
                cancellationToken);
            await processor.PrepareBatchAsync(
                batch,
                pendingItems,
                cancellationToken);

            logger.LogInformation(
                "Processando lote {BatchId}: {PendingCount} itens pendentes de {TotalCount} total.",
                batchId,
                pendingItems.Length,
                items.Count);

            var processorCheckpointsSinceSave = 0;
            var itemsSinceOperationalCheck = 0;
            var itemsSinceBatchCheckpoint = 0;
            var lastBatchCheckpointItemId = Guid.Empty;
            var batchCancelled = false;
            DateTimeOffset? earliestTransientRetryAt = null;

            async Task CheckpointProcessorAsync(CancellationToken checkpointCancellationToken)
            {
                // Context and analysis transitions stay on the tracked item,
                // but flushing every transition turns a 10k run into tens of
                // thousands of database round trips. Periodic and final saves
                // bound restart loss without multiplying database pressure.
                checkpointCancellationToken.ThrowIfCancellationRequested();
                processorCheckpointsSinceSave++;
                if (jobStore is null && processorCheckpointsSinceSave >= CheckpointInterval)
                {
                    await repository.SaveChangesAsync(checkpointCancellationToken);
                    processorCheckpointsSinceSave = 0;
                }
            }

            // Claims are deliberately windowed. Claiming an entire 400-item
            // batch at once would let early leases expire while later items
            // wait behind slow AI calls, allowing another replica to take the
            // same work. Sixteen items keeps the claim horizon bounded while
            // reducing claim/release round trips by an order of magnitude.
            foreach (var itemWindow in pendingItems.Chunk(ItemClaimWindowSize))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var windowClaimedItemIds = jobStore is null
                    ? itemWindow.Select(item => item.Id).ToHashSet()
                    : (await jobStore.TryClaimItemsAsync(
                        batchId,
                        itemWindow.Select(item => item.Id).ToArray(),
                        WorkerId,
                        DateTimeOffset.UtcNow,
                        LeaseDuration,
                        cancellationToken)).ToHashSet();
                var claimedItemIds = windowClaimedItemIds.ToArray();
                var transientRetries = new Dictionary<Guid, (DateTimeOffset NextAttemptAt, string? ErrorPhase)>();

                foreach (var item in itemWindow.Where(item => windowClaimedItemIds.Contains(item.Id)))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    itemsSinceOperationalCheck++;
                    // Verificar cancelamento em intervalos amortizados. O
                    // lease do lote continua protegendo contra outra réplica;
                    // a leitura por item era um N+1 desnecessário.
                    if (itemsSinceOperationalCheck >= CheckpointInterval)
                    {
                        itemsSinceOperationalCheck = 0;
                        var currentBatch = await repository.GetBatchAsync(batchId, cancellationToken);
                        if (currentBatch?.Status == GradingBatchStatus.Cancelled)
                        {
                            if (jobStore is not null)
                            {
                                await jobStore.ReleaseBatchLeaseAsync(
                                    batchId,
                                    WorkerId,
                                    DateTimeOffset.UtcNow,
                                    errorCode: null,
                                    nextAttemptAt: null,
                                    cancellationToken);
                            }

                            logger.LogInformation(
                                "Lote {BatchId} cancelado durante processamento; interrompendo worker.",
                                batchId);
                            batchCancelled = true;
                            break;
                        }
                    }

                    async Task ProcessItemCoreAsync(CancellationToken itemCancellationToken)
                    {
                        item.MarkProcessingStage(GradingProcessingStage.Ingestion);

                        await ingestionService.IngestPendingAsync(
                            batch,
                            item,
                            itemCancellationToken);

                        await processor.ProcessItemAsync(
                            item,
                            repository,
                            itemCancellationToken,
                            batch.TeacherInstructions,
                            new GradingContextOptions(
                                batch.IncludeRubric,
                                batch.IncludeSubmissionFiles,
                                batch.IncludeCourseMaterials,
                                batch.TeacherInstructions),
                            checkpointAsync: CheckpointProcessorAsync);
                        item.MarkProcessingStage(
                            item.Status == GradingItemStatus.AwaitingAiAnalysis
                                ? GradingProcessingStage.Analysis
                                : item.Status is GradingItemStatus.Blocked or GradingItemStatus.Failed
                                    ? GradingProcessingStage.Failed
                                    : GradingProcessingStage.Completed);
                    }

                    try
                    {
                        if (jobStore is null)
                        {
                            await ProcessItemCoreAsync(cancellationToken);
                        }
                        else if (!await ProcessItemWithLeaseHeartbeatAsync(
                                     jobStore,
                                     batchId,
                                     item.Id,
                                     claimedItemIds,
                                     cancellationToken,
                                     ProcessItemCoreAsync,
                                     getRenewableItemIds: () => claimedItemIds))
                        {
                            logger.LogWarning(
                                "Lease do item {GradingItemId} ou do lote {BatchId} foi perdido; descartando o restante deste worker.",
                                item.Id,
                                batchId);
                            return;
                        }

                        lastBatchCheckpointItemId = item.Id;
                        itemsSinceBatchCheckpoint++;
                        if (jobStore is not null && itemsSinceBatchCheckpoint >= CheckpointInterval)
                        {
                            // A checkpoint is also the amortized heartbeat
                            // for fast items. If every item finishes before
                            // the per-item timer ticks, renewing only inside
                            // long calls would let the 15-minute batch lease
                            // expire during a long 10k run.
                            var batchLeaseRenewed = await jobStore.RenewBatchLeaseAsync(
                                batchId,
                                WorkerId,
                                DateTimeOffset.UtcNow,
                                LeaseDuration,
                                cancellationToken);
                            if (!batchLeaseRenewed)
                            {
                                logger.LogWarning(
                                    "Lease do lote {BatchId} expirou antes do heartbeat de checkpoint; descartando alterações locais.",
                                    batchId);
                                return;
                            }

                            var checkpointed = await jobStore.UpdateBatchCheckpointAsync(
                                batchId,
                                WorkerId,
                                lastBatchCheckpointItemId,
                                DateTimeOffset.UtcNow,
                                cancellationToken);
                            if (!checkpointed)
                            {
                                logger.LogWarning(
                                    "Lease do lote {BatchId} expirou antes do checkpoint; descartando alterações locais.",
                                    batchId);
                                return;
                            }

                            itemsSinceBatchCheckpoint = 0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (ex is GradingTransientDependencyException transient)
                        {
                            var nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(
                                Math.Clamp(limits.Value.TransientProcessingRetryDelaySeconds, 5, 3600));
                            item.ScheduleProcessingRetry(
                                "Dependencia temporariamente indisponivel; nova tentativa agendada.",
                                nextAttemptAt,
                                transient.ErrorCode,
                                GradingFailurePhase.FromProcessingStage(item.ProcessingStage));
                            transientRetries[item.Id] = (nextAttemptAt, item.LastErrorPhase);
                            earliestTransientRetryAt = earliestTransientRetryAt is null ||
                                nextAttemptAt < earliestTransientRetryAt
                                ? nextAttemptAt
                                : earliestTransientRetryAt;
                            // The lease is released below with the individual
                            // retry time. Do not let the bulk release erase it.
                            windowClaimedItemIds.Remove(item.Id);
                            logger.LogWarning(
                                "Dependência temporariamente indisponível para o item {GradingItemId} na fase {ErrorPhase}, código {ErrorCode}; nova tentativa agendada para {NextAttemptAt}.",
                                item.Id,
                                item.LastErrorPhase,
                                item.LastErrorCode,
                                nextAttemptAt);
                        }
                        else
                        {
                            var errorDescriptor = MoodleErrorContract.Describe(ex);
                            logger.LogError(
                                "Falha recuperável ao processar item {GradingItemId} do lote {BatchId}, fase {ErrorPhase}, código {ErrorCode}.",
                                item.Id,
                                batchId,
                                GradingFailurePhase.FromProcessingStage(item.ProcessingStage),
                                errorDescriptor.ErrorCode);
                            item.MarkAnalysisFailed(
                                $"Falha ao processar este item de correcao assistida: {errorDescriptor.Message}",
                                errorDescriptor.ErrorCode,
                                GradingFailurePhase.FromProcessingStage(item.ProcessingStage));
                            item.MarkProcessingStage(GradingProcessingStage.Failed);
                        }
                    }

                    if (jobStore is not null && processorCheckpointsSinceSave >= CheckpointInterval)
                    {
                        var saved = await repository.SaveChangesWithActiveLeasesAsync(
                            batchId,
                            WorkerId,
                            claimedItemIds,
                            DateTimeOffset.UtcNow,
                            LeaseDuration,
                            cancellationToken);
                        if (!saved)
                        {
                            logger.LogWarning(
                                "Lease do lote {BatchId} ou da janela de itens expirou antes do checkpoint; descartando alterações locais.",
                                batchId);
                            return;
                        }

                        processorCheckpointsSinceSave = 0;
                    }
                }

                // The window's leases are no longer needed after its items
                // have been processed. A bulk release avoids one UPDATE per
                // item while keeping interrupted windows recoverable by TTL.
                if (jobStore is not null &&
                    (windowClaimedItemIds.Count > 0 || transientRetries.Count > 0))
                {
                    var saved = await repository.SaveChangesWithActiveLeasesAsync(
                        batchId,
                        WorkerId,
                        claimedItemIds,
                        DateTimeOffset.UtcNow,
                        LeaseDuration,
                        cancellationToken);
                    if (!saved)
                    {
                        logger.LogWarning(
                            "Lease do lote {BatchId} ou da janela de itens expirou antes de salvar; descartando alterações locais.",
                            batchId);
                        return;
                    }

                    processorCheckpointsSinceSave = 0;

                    foreach (var retry in transientRetries)
                    {
                        await jobStore.ReleaseItemLeaseAsync(
                            batchId,
                            retry.Key,
                            WorkerId,
                            DateTimeOffset.UtcNow,
                            errorCode: "transient_dependency",
                            retry.Value.NextAttemptAt,
                            cancellationToken,
                            errorPhase: retry.Value.ErrorPhase);
                    }

                    await jobStore.ReleaseItemLeasesAsync(
                        batchId,
                        windowClaimedItemIds,
                        WorkerId,
                        DateTimeOffset.UtcNow,
                        errorCode: null,
                        nextAttemptAt: null,
                        cancellationToken);
                }

                if (batchCancelled)
                {
                    break;
                }
            }

            if (jobStore is not null && lastBatchCheckpointItemId != Guid.Empty)
            {
                var checkpointed = await jobStore.UpdateBatchCheckpointAsync(
                    batchId,
                    WorkerId,
                    lastBatchCheckpointItemId,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                if (!checkpointed)
                {
                    logger.LogWarning("Lease do lote {BatchId} expirou antes do checkpoint final.", batchId);
                    return;
                }
            }

            // Recarregar todos para contadores atualizados.
            if (jobStore is not null && !await jobStore.RenewBatchLeaseAsync(
                    batchId,
                    WorkerId,
                    DateTimeOffset.UtcNow,
                    LeaseDuration,
                    cancellationToken))
            {
                logger.LogWarning("Lease do lote {BatchId} expirou antes de salvar os contadores.", batchId);
                return;
            }

            var allItems = await GradingItemProcessor.LoadAllBatchItemsAsync(
                repository,
                batchId,
                cancellationToken,
                maxItems);
            GradingItemProcessor.UpdateBatchCounters(batch, allItems);
            await repository.SaveChangesAsync(cancellationToken);

            if (jobStore is not null)
            {
                await jobStore.ReleaseBatchLeaseAsync(
                    batchId,
                    WorkerId,
                    DateTimeOffset.UtcNow,
                    errorCode: earliestTransientRetryAt is null ? null : "transient_dependency",
                    nextAttemptAt: earliestTransientRetryAt,
                    cancellationToken);
            }

            logger.LogInformation(
                "Lote {BatchId} processado: {ReadyItems} prontos, {BlockedItems} bloqueados, {FailedItems} falhos.",
                batchId,
                batch.ReadyItems,
                batch.BlockedItems,
                batch.FailedItems);
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

            connectionGate.Release();
        }
    }

    private async Task AdaptConcurrencyAsync(CancellationToken cancellationToken)
    {
        if (!limits.Value.AdaptiveBatchWorkerConcurrency)
        {
            return;
        }

        var intervalSeconds = Math.Clamp(limits.Value.BatchWorkerScaleIntervalSeconds, 2, 60);
        var idleScaleDown = TimeSpan.FromSeconds(
            Math.Clamp(limits.Value.BatchWorkerIdleScaleDownSeconds, intervalSeconds, 3600));
        var minimum = ResolveMinimumConcurrency();
        var maximum = ResolveMaximumConcurrency();
        var cpuTarget = ResolveCpuTargetPercent();
        var memoryTarget = ResolveMemoryTargetPercent();
        TimeSpan? previousCpu = null;
        DateTimeOffset? previousSampleAt = null;
        DateTimeOffset? idleSince = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                var (cpuPercent, memoryPercent, currentCpu) = ReadCapacitySample();
                var elapsed = previousSampleAt is null ? TimeSpan.Zero : now - previousSampleAt.Value;
                var cpu = cpuPercent;
                if (previousCpu is not null && currentCpu is not null && elapsed > TimeSpan.Zero)
                {
                    cpu = Math.Clamp(
                        (currentCpu.Value - previousCpu.Value).TotalMilliseconds /
                        (elapsed.TotalMilliseconds * Math.Max(1, Environment.ProcessorCount)) * 100d,
                        0d,
                        100d);
                }

                previousCpu = currentCpu;
                previousSampleAt = now;

                var backlog = channel.PendingCount + activeBatches.Count;
                var currentTarget = concurrencyGate.Target;
                var desiredTarget = currentTarget;
                var cpuPressure = cpu.HasValue && cpu.Value >= Math.Min(100, cpuTarget + 5);
                var memoryPressure = memoryPercent.HasValue && memoryPercent.Value >= Math.Min(100, memoryTarget + 5);
                var cpuComfortable = cpu is null || cpu <= Math.Max(0, cpuTarget - 10);
                var memoryComfortable = memoryPercent is null || memoryPercent <= Math.Max(0, memoryTarget - 10);

                if (backlog == 0 && concurrencyGate.Active == 0)
                {
                    idleSince ??= now;
                    if (now - idleSince >= idleScaleDown && currentTarget > minimum)
                    {
                        desiredTarget = currentTarget - 1;
                    }
                }
                else
                {
                    idleSince = null;
                    if ((cpuPressure || memoryPressure) && currentTarget > minimum)
                    {
                        desiredTarget = currentTarget - 1;
                    }
                    else if (backlog >= currentTarget && cpuComfortable && memoryComfortable && currentTarget < maximum)
                    {
                        desiredTarget = currentTarget + 1;
                    }
                }

                if (desiredTarget == currentTarget)
                {
                    continue;
                }

                concurrencyGate.SetTarget(desiredTarget);
                logger.LogInformation(
                    "Pool adaptativo ajustado de {PreviousConcurrency} para {CurrentConcurrency}; fila={Backlog}, ativos={ActiveBatches}, cpu={CpuPercent:F1}%, memoria={MemoryPercent:F1}%.",
                    currentTarget,
                    desiredTarget,
                    backlog,
                    concurrencyGate.Active,
                    cpu ?? -1,
                    memoryPercent ?? -1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Encerramento normal do host.
        }
    }

    private (double? CpuPercent, double? MemoryPercent, TimeSpan? CurrentCpu) ReadCapacitySample()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var currentCpu = process.TotalProcessorTime;
            var memory = GC.GetGCMemoryInfo();
            var memoryPercent = memory.TotalAvailableMemoryBytes > 0
                ? Math.Clamp(
                    memory.MemoryLoadBytes * 100d / memory.TotalAvailableMemoryBytes,
                    0d,
                    100d)
                : (double?)null;
            return (null, memoryPercent, currentCpu);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private int ResolveMinimumConcurrency() =>
        Math.Clamp(limits.Value.BatchWorkerMinConcurrency, 1, 32);

    private int ResolveMaximumConcurrency()
    {
        var minimum = ResolveMinimumConcurrency();
        if (limits.Value.BatchWorkerMaxConcurrency > 0)
        {
            return Math.Clamp(limits.Value.BatchWorkerMaxConcurrency, minimum, 32);
        }

        // Environment.ProcessorCount respects the CPU quota of the Linux
        // container/VPS. Four async-heavy batches per logical processor is a
        // safe upper bound; CPU/memory feedback still keeps the actual target
        // near the configured 80% budget.
        var cpuMaximum = Math.Clamp(
            Math.Max(1, Environment.ProcessorCount) * 4,
            minimum,
            32);
        var memoryMaximum = cpuMaximum;
        var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var memoryPerWorker = Math.Max(64L, limits.Value.BatchWorkerMemoryPerWorkerMb) * 1024L * 1024L;
        if (totalMemory > 0)
        {
            memoryMaximum = (int)Math.Clamp(totalMemory / memoryPerWorker, minimum, 32L);
        }

        return Math.Clamp(Math.Min(cpuMaximum, memoryMaximum), minimum, 32);
    }

    private int ResolveInitialConcurrency(int maximum) =>
        Math.Clamp(limits.Value.BatchWorkerConcurrency, ResolveMinimumConcurrency(), maximum);

    private int ResolveCpuTargetPercent() =>
        Math.Clamp(limits.Value.BatchWorkerCpuTargetPercent, 20, 95);

    private int ResolveMemoryTargetPercent() =>
        Math.Clamp(limits.Value.BatchWorkerMemoryTargetPercent, 20, 95);

    private static string ResolveConnectionKey(AssistedGradingBatch batch) =>
        !string.IsNullOrWhiteSpace(batch.MoodleConnectionId)
            ? $"connection:{batch.MoodleConnectionId.Trim()}"
            : $"client:{(string.IsNullOrWhiteSpace(batch.ConnectorClientId) ? "default" : batch.ConnectorClientId)}" +
              $":alias:{(string.IsNullOrWhiteSpace(batch.ConnectionAlias) ? "default" : batch.ConnectionAlias)}";

    private async Task<bool> ProcessItemWithLeaseHeartbeatAsync(
        IGradingBatchJobStore jobStore,
        Guid batchId,
        Guid itemId,
        IReadOnlyCollection<Guid> windowItemIds,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> work,
        Func<bool>? shouldRenewItem = null,
        Func<IReadOnlyCollection<Guid>>? getRenewableItemIds = null)
    {
        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLost = 0;
        var heartbeat = MaintainLeaseHeartbeatAsync(
            jobStore,
            batchId,
            itemId,
            windowItemIds,
            leaseCancellation,
            () => Interlocked.Exchange(ref leaseLost, 1),
            shouldRenewItem,
            getRenewableItemIds);

        try
        {
            await work(leaseCancellation.Token);
            return Volatile.Read(ref leaseLost) == 0;
        }
        finally
        {
            leaseCancellation.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // Cancellation belongs to the linked work token and is
                // observed by the caller when the item operation is aborted.
            }
        }
    }

    private async Task MaintainLeaseHeartbeatAsync(
        IGradingBatchJobStore jobStore,
        Guid batchId,
        Guid itemId,
        IReadOnlyCollection<Guid> windowItemIds,
        CancellationTokenSource leaseCancellation,
        Action markLeaseLost,
        Func<bool>? shouldRenewItem,
        Func<IReadOnlyCollection<Guid>>? getRenewableItemIds)
    {
        var interval = TimeSpan.FromTicks(Math.Max(
            TimeSpan.FromSeconds(5).Ticks,
            LeaseDuration.Ticks / 3));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(leaseCancellation.Token))
            {
                var now = DateTimeOffset.UtcNow;
                if (!await jobStore.RenewBatchLeaseAsync(
                        batchId,
                        WorkerId,
                        now,
                        LeaseDuration,
                        leaseCancellation.Token))
                {
                    logger.LogWarning(
                        "Lease do lote {BatchId} expirou durante o processamento do item {GradingItemId}.",
                        batchId,
                        itemId);
                    markLeaseLost();
                    leaseCancellation.Cancel();
                    return;
                }

                // Keep queued items in this claim window owned by the same
                // worker. Without this bulk heartbeat, a slow first item
                // could let later queued items expire and be claimed by a
                // second replica before this worker reaches them.
                var renewableItemIds = getRenewableItemIds?.Invoke() ?? windowItemIds;
                if (renewableItemIds.Count > 0)
                {
                    var renewedItemCount = await jobStore.RenewItemLeasesAsync(
                        batchId,
                        renewableItemIds,
                        WorkerId,
                        now,
                        LeaseDuration,
                        leaseCancellation.Token);
                    if (renewedItemCount < renewableItemIds.Count)
                    {
                        // A short renewal means another replica acquired at
                        // least one still-pending item after its lease
                        // expired; abort before this worker can write a
                        // duplicate.
                        logger.LogWarning(
                            "A janela do lote {BatchId} perdeu {LostCount} claim(s) durante o item {GradingItemId}.",
                            batchId,
                            renewableItemIds.Count - renewedItemCount,
                            itemId);
                        markLeaseLost();
                        leaseCancellation.Cancel();
                        return;
                    }
                }

                // Once the processor has moved the item out of Pending, its
                // lease no longer needs renewal. Avoid treating that normal
                // transition as a lost lease when the heartbeat races the
                // final item update.
                if (shouldRenewItem is not null && !shouldRenewItem())
                {
                    continue;
                }

                if (!await jobStore.RenewItemLeaseAsync(
                        batchId,
                        itemId,
                        WorkerId,
                        now,
                        LeaseDuration,
                        leaseCancellation.Token))
                {
                    logger.LogWarning(
                        "Lease do item {GradingItemId} expirou durante o processamento do lote {BatchId}.",
                        itemId,
                        batchId);
                    markLeaseLost();
                    leaseCancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
        {
            // Normal completion or cancellation of the item work.
        }
        catch (Exception exception)
        {
            // A heartbeat failure is safety-critical: stop this worker rather
            // than risk writing after another replica has acquired the lease.
            logger.LogWarning(
                "Falha ao renovar lease do lote {BatchId}/item {GradingItemId}; interrompendo o processamento local. ErrorCode={ErrorCode}",
                batchId,
                itemId,
                MoodleErrorContract.NormalizeCode(MoodleErrorContract.Describe(exception).ErrorCode));
            markLeaseLost();
            leaseCancellation.Cancel();
        }
    }

    private TimeSpan LeaseDuration => TimeSpan.FromMinutes(
        Math.Clamp(limits.Value.BatchLeaseMinutes, 1, 120));
}
