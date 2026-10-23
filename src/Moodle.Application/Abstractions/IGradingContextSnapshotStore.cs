using Moodle.Domain.Grading;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Armazenamento append-only da identidade e do payload operacional do contexto.
/// A implementação deve ser idempotente para a mesma combinação item/versão/hash.
/// </summary>
public interface IGradingContextSnapshotStore
{
    Task PublishAsync(
        GradingContextSnapshot snapshot,
        CancellationToken cancellationToken);
}
