using System.Collections.Concurrent;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Retoma publicações de notas que já foram autorizadas, mas não chegaram a
/// concluir a execução HTTP. A autorização permanece uma decisão humana; este
/// worker somente reexecuta o payload durável usando os mesmos leases e claims
/// do handler síncrono.
/// </summary>
public sealed class GradingPublicationWorkerService(
    IServiceScopeFactory scopeFactory,
    IOptions<GradingLimitsOptions> limits,
    ILogger<GradingPublicationWorkerService> logger,
    IMoodleAdmissionWorkScope? admissionWorkScope = null) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, byte> activeActions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> connectionGates = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string WorkerId =
        $"publication:{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GradingPublicationWorkerService iniciado.");
        var pollSeconds = Math.Clamp(limits.Value.PublicationWorkerPollSeconds, 1, 300);
        var concurrency = Math.Clamp(limits.Value.PublicationWorkerConcurrency, 1, 16);

        try
        {
            // Executa uma varredura imediata para reduzir a janela de crash
            // entre a autorização e o primeiro tick do timer.
            await PollWithRetryOnNextTickAsync(concurrency, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PollWithRetryOnNextTickAsync(concurrency, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Falha fatal no worker de retomada de publicações Moodle. ErrorCode={ErrorCode}",
                MoodleErrorContract.Describe(exception).ErrorCode);
        }
        finally
        {
            logger.LogInformation("GradingPublicationWorkerService encerrado.");
        }
    }

    private async Task PollWithRetryOnNextTickAsync(int concurrency, CancellationToken cancellationToken)
    {
        try
        {
            await PollOnceAsync(concurrency, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A falha transitória de banco/rede não pode desligar o único
            // mecanismo automático de retomada. O próximo tick repete a
            // consulta e os leases continuam protegendo a exclusividade.
            logger.LogWarning(
                "Falha transitória ao consultar publicações recuperáveis; nova tentativa no próximo ciclo. ErrorCode={ErrorCode}",
                MoodleErrorContract.Describe(exception).ErrorCode);
        }
    }

    private async Task PollOnceAsync(int concurrency, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var pendingActions = scope.ServiceProvider.GetService<IPendingMoodleActionRepository>();
        if (pendingActions is null)
        {
            return;
        }

        // The action and its target claims are persisted by separate
        // repositories. If a process crashed after marking the action
        // terminal but before releasing claims, repair that small window
        // before looking for new work. The operation is idempotent.
        var gradingRepository = scope.ServiceProvider.GetService<IGradingReviewRepository>();
        if (gradingRepository is not null)
        {
            var terminalPublicationIds = await pendingActions.ListTerminalGradingPublicationIdsAsync(
                Math.Clamp(concurrency * 4, 1, 1000),
                cancellationToken);
            foreach (var publicationId in terminalPublicationIds)
            {
                await gradingRepository.ReleasePublicationClaimsAsync(publicationId, cancellationToken);
            }
        }

        var recoverable = await pendingActions.ListRecoverableGradingPublicationsAsync(
            DateTimeOffset.UtcNow,
            Math.Clamp(concurrency * 2, 1, 100),
            cancellationToken);
        if (recoverable.Count == 0)
        {
            return;
        }

        using var limiter = new SemaphoreSlim(concurrency, concurrency);
        var tasks = recoverable.Select(async action =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                await ProcessActionAsync(action.Id, cancellationToken);
            }
            finally
            {
                limiter.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task ProcessActionAsync(Guid actionId, CancellationToken cancellationToken)
    {
        if (!activeActions.TryAdd(actionId, 0))
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var pendingActions = scope.ServiceProvider.GetRequiredService<IPendingMoodleActionRepository>();
            var action = await pendingActions.GetByIdAsync(actionId, cancellationToken);
            if (action is null)
            {
                return;
            }

            GradingLaunchPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<GradingLaunchPayload>(action.PayloadJson);
            }
            catch (JsonException)
            {
                logger.LogError("Payload persistido de publicação é inválido; retomada ignorada. FailureReason={FailureReason}", "invalid_json");
                return;
            }

            if (payload is null || payload.BatchJobId == Guid.Empty)
            {
                logger.LogError("Payload de publicação {ActionId} não possui batchJobId válido; retomada ignorada.", actionId);
                return;
            }

            var gradingRepository = scope.ServiceProvider.GetRequiredService<IGradingReviewRepository>();
            var batch = await gradingRepository.GetBatchAsync(payload.BatchJobId, cancellationToken);
            if (batch is null)
            {
                var run = await gradingRepository.GetGradingRunAsync(payload.BatchJobId, cancellationToken);
                var children = run is null
                    ? []
                    : await gradingRepository.ListBatchesByGradingRunAsync(run.Id, cancellationToken);
                batch = children.FirstOrDefault();
            }

            if (batch is null || string.IsNullOrWhiteSpace(batch.ConnectorClientId))
            {
                logger.LogWarning(
                    "Publicação {ActionId} ainda não pode ser retomada: conexão do lote não está disponível.",
                    actionId);
                return;
            }

            var executionContext = scope.ServiceProvider.GetRequiredService<IConnectorExecutionContext>();
            var connectionSelection = scope.ServiceProvider.GetService<IMoodleConnectionSelection>();
            var connectionKey = ResolveConnectionKey(batch);
            var gateSize = Math.Clamp(limits.Value.PublicationWorkerPerConnectionConcurrency, 1, 16);
            var connectionGate = connectionGates.GetOrAdd(
                connectionKey,
                _ => new SemaphoreSlim(gateSize, gateSize));
            await connectionGate.WaitAsync(cancellationToken);
            var connectionGateHeld = true;
            try
            {
                executionContext.Enter(
                    batch.ConnectorClientId,
                    action.CreatedBySubject,
                    action.CreatedByEmail,
                    ["moodle.write.assignments.grade"]);
                if (connectionSelection is not null)
                {
                    connectionSelection.Alias = batch.ConnectionAlias;
                }
                var admissionOwnerId = ResolveAdmissionOwnerId(executionContext.Subject, executionContext.ClientId);
                using var backgroundAdmission = admissionWorkScope is not null &&
                                                admissionOwnerId.Length is > 0 and <= 128
                    ? admissionWorkScope.Begin(
                        MoodleAdmissionOrigin.Grading,
                        admissionOwnerId,
                        MoodleAdmissionPriority.Background)
                    : null;

                try
                {
                    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                    var result = await mediator.Send(
                        new ConfirmMoodleBatchLaunchCommand(
                            action.Id,
                            action.ConfirmationText,
                            ExecuteImmediately: true),
                        cancellationToken);
                    logger.LogInformation(
                        "Publicação {ActionId} retomada pelo worker com status {Status}; enviados={SentItems}, falhos={FailedItems}.",
                        actionId,
                        result.Status,
                        result.SentItems,
                        result.FailedItems);
                }
                finally
                {
                    if (connectionSelection is not null)
                    {
                        connectionSelection.Alias = null;
                    }
                    executionContext.Clear();
                }
            }
            finally
            {
                if (connectionGateHeld)
                {
                    connectionGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // O lease expira naturalmente e outra réplica poderá tentar de
            // novo. Não marcar Failed aqui preserva a distinção entre erro
            // transitório do worker e falha remota definitiva por item.
            logger.LogWarning(
                "Falha ao retomar publicação {ActionId}; nova tentativa será feita pelo lease. ErrorCode={ErrorCode}",
                actionId,
                MoodleErrorContract.Describe(exception).ErrorCode);
        }
        finally
        {
            activeActions.TryRemove(actionId, out _);
        }
    }

    private static string ResolveConnectionKey(AssistedGradingBatch batch) =>
        !string.IsNullOrWhiteSpace(batch.MoodleConnectionId)
            ? $"connection:{batch.MoodleConnectionId.Trim()}"
            : $"client:{(string.IsNullOrWhiteSpace(batch.ConnectorClientId) ? "default" : batch.ConnectorClientId)}" +
              $":alias:{(string.IsNullOrWhiteSpace(batch.ConnectionAlias) ? "default" : batch.ConnectionAlias)}";

    private static string ResolveAdmissionOwnerId(string? subject, string? clientId)
    {
        if (Guid.TryParse(subject, out var ownerId)) return ownerId.ToString("N");
        return !string.IsNullOrWhiteSpace(clientId) ? clientId : subject ?? string.Empty;
    }
}
