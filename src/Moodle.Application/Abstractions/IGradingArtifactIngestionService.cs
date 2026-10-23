using Moodle.Domain.Grading;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Completa as referências de um item de correção fora do request HTTP.
/// A entrega dos arquivos ocorre diretamente pelo MCP Resource no chat; este
/// contrato não possui operação de download ou extração local.
/// </summary>
public interface IGradingArtifactIngestionService
{
    /// <summary>
    /// Retorna as referências já carregadas para o item no escopo do worker.
    /// <c>null</c> significa que o item ainda não foi preparado; uma lista
    /// vazia é uma resposta válida e evita um SELECT redundante depois da
    /// ingestão (inclusive quando a ingestão deferida não encontrou arquivos).
    /// </summary>
    IReadOnlyList<GradingArtifact>? TryGetCachedArtifacts(Guid gradingItemId) => null;

    /// <summary>
    /// Permite que uma implementação carregue artifacts de um sublote em uma
    /// única leitura antes da iteração. Implementações legadas podem manter o
    /// no-op e continuar usando o caminho individual.
    /// </summary>
    Task PrepareBatchAsync(
        AssistedGradingBatch batch,
        IReadOnlyCollection<Guid> gradingItemIds,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task IngestPendingAsync(
        AssistedGradingBatch batch,
        AssistedGradingItem item,
        CancellationToken cancellationToken);

}
