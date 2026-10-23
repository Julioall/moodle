namespace Moodle.Application.Abstractions;

/// <summary>
/// Aplica a retenção de conteúdo bruto sem remover a identidade do artifact,
/// hashes, estados de extração, cobertura ou evidência de revisão.
/// </summary>
public interface IGradingRetentionStore
{
    Task<int> RedactExpiredArtifactTextAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken);
}
