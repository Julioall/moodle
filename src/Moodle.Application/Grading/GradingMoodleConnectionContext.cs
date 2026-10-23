using Moodle.Application.Abstractions;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public static class GradingMoodleConnectionContext
{
    public static async Task<AssistedGradingBatch> RestorePublicationAsync(
        IGradingReviewRepository repository,
        ICurrentUserContext currentUser,
        Guid batchJobId,
        IConnectorExecutionContext? executionContext,
        IMoodleConnectionSelection? connectionSelection,
        IMoodleBackgroundConnectionContextRestorer? backgroundConnectionContextRestorer,
        CancellationToken cancellationToken,
        Guid? expectedGradingItemId = null)
    {
        var scope = await GradingBatchScopeResolver.ResolveAsync(
            repository,
            currentUser,
            batchJobId,
            cancellationToken);
        var batch = scope.FirstBatch
            ?? throw new InvalidOperationException("A execução de correção não possui sublotes para reconciliar.");
        var connectionKeys = scope.Batches
            .Select(GradingBatchScopeResolver.ResolveConnectionKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (connectionKeys.Length > 1)
        {
            throw new InvalidOperationException("Uma reconciliação não pode misturar lotes de conexões Moodle diferentes.");
        }

        if (expectedGradingItemId is Guid gradingItemId)
        {
            var hasCapturedConnection = !string.IsNullOrWhiteSpace(batch.MoodleConnectionId) ||
                (!string.IsNullOrWhiteSpace(batch.ConnectorClientId) && !string.IsNullOrWhiteSpace(batch.ConnectionAlias));
            if (!hasCapturedConnection)
            {
                throw new InvalidOperationException("A conexão original do lote não foi capturada; nenhuma consulta Moodle foi realizada.");
            }

            var item = await repository.GetItemAsync(gradingItemId, cancellationToken)
                ?? throw new InvalidOperationException("O item informado não pertence à execução de correção autorizada.");
            if (!scope.Batches.Any(candidate => candidate.Id == item.BatchId))
            {
                throw new InvalidOperationException("O item informado não pertence à execução de correção autorizada.");
            }
        }

        await RestoreAsync(
            batch,
            executionContext,
            connectionSelection,
            backgroundConnectionContextRestorer,
            cancellationToken);
        return batch;
    }

    public static async Task RestoreAsync(
        AssistedGradingBatch batch,
        IConnectorExecutionContext? executionContext,
        IMoodleConnectionSelection? connectionSelection,
        IMoodleBackgroundConnectionContextRestorer? backgroundConnectionContextRestorer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!string.IsNullOrWhiteSpace(batch.MoodleConnectionId))
        {
            if (backgroundConnectionContextRestorer is null ||
                !await backgroundConnectionContextRestorer.TryRestoreAsync(
                    batch.MoodleConnectionId,
                    batch.CreatedBySubject,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "A conexão Moodle original deste lote não está disponível; nenhuma consulta ou lançamento foi realizado.");
            }

            return;
        }

        var hasConnectorClient = !string.IsNullOrWhiteSpace(batch.ConnectorClientId);
        var hasConnectionAlias = !string.IsNullOrWhiteSpace(batch.ConnectionAlias);
        if (!hasConnectorClient && !hasConnectionAlias)
        {
            return;
        }

        if (hasConnectorClient)
        {
            if (executionContext is null)
            {
                throw new InvalidOperationException(
                    "O contexto do cliente que criou este lote não pode ser restaurado; nenhuma consulta ou lançamento foi realizado.");
            }

            executionContext.Enter(batch.ConnectorClientId!, batch.CreatedBySubject, email: null);
        }

        if (connectionSelection is null)
        {
            throw new InvalidOperationException(
                "A conexão Moodle original deste lote não pode ser selecionada; nenhuma consulta ou lançamento foi realizado.");
        }

        // Clear a possibly different request selection when the batch captured
        // the default alias. The stored connection identity, not the current
        // MCP call's implicit default, decides where grading reads go.
        connectionSelection.Alias = batch.ConnectionAlias;
    }
}
