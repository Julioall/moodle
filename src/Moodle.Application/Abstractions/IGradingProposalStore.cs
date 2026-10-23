using Moodle.Domain.Grading;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Armazena propostas de correção versionadas sem substituir versões anteriores.
/// A implementação deve ser idempotente para o mesmo item/versão/hash.
/// </summary>
public interface IGradingProposalStore
{
    Task<AiGradingProposalDocument?> GetLatestDocumentAsync(
        Guid gradingItemId,
        CancellationToken cancellationToken) => Task.FromResult<AiGradingProposalDocument?>(null);

    Task<int> GetNextVersionAsync(Guid gradingItemId, CancellationToken cancellationToken);

    async Task<IReadOnlyDictionary<Guid, int>> GetNextVersionsAsync(
        IReadOnlyCollection<Guid> gradingItemIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var id in gradingItemIds)
        {
            result[id] = await GetNextVersionAsync(id, cancellationToken);
        }
        return result;
    }

    Task PublishAsync(AiGradingProposal proposal, CancellationToken cancellationToken);

    async Task PublishManyAsync(
        IReadOnlyCollection<AiGradingProposal> proposals,
        CancellationToken cancellationToken)
    {
        foreach (var proposal in proposals)
        {
            await PublishAsync(proposal, cancellationToken);
        }
    }
}
