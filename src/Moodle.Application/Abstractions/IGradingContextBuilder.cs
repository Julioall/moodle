using Moodle.Domain.Grading;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Monta o contexto mínimo de correção para um item de lote a partir
/// dos dados disponíveis localmente e de gateways configurados.
/// </summary>
public interface IGradingContextBuilder
{
    /// <summary>
    /// Permite pré-carregar dados compartilhados por um sublote antes da
    /// iteração dos itens. A implementação padrão mantém compatibilidade com
    /// builders legados; o builder de produção usa uma leitura em lote para
    /// evitar N+1 de artifacts, lotes e configurações Moodle.
    /// </summary>
    Task PrepareBatchAsync(
        AssistedGradingBatch batch,
        IReadOnlyCollection<AssistedGradingItem> items,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Monta o contexto de correção para o item dado.
    /// Retorna objeto com bloqueadores quando dados mínimos não estão disponíveis.
    /// </summary>
    Task<GradingContext> BuildAsync(
        AssistedGradingItem item,
        GradingContextOptions options,
        CancellationToken cancellationToken);
}

public sealed record GradingContextOptions(
    bool IncludeRubric = true,
    bool IncludeSubmissionFiles = true,
    bool IncludeCourseMaterials = false,
    string? TeacherInstructions = null);
