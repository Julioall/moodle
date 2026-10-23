namespace Moodle.Application.Abstractions;

/// <summary>
/// Gera critérios avaliativos estruturados a partir do contexto da atividade.
/// Implementações podem usar heurísticas locais ou modelos de IA/LLM.
/// </summary>
public interface ICriteriaGenerationService
{
    Task<CriteriaGenerationResult> GenerateAsync(
        CriteriaGenerationRequest request,
        CancellationToken cancellationToken);
}

public sealed record CriteriaGenerationRequest(
    string AssignmentName,
    string? AssignmentDescription,
    string? ContextText,
    string? SupportingMaterials,
    decimal MaxGrade);

public sealed record CriteriaGenerationResult(
    string Source,
    decimal MaxPoints,
    decimal Confidence,
    IReadOnlyList<GeneratedCriterion> Criteria,
    IReadOnlyList<string> Warnings,
    string? PrivateNotesToTeacher);

public sealed record GeneratedCriterion(
    string Id,
    string Description,
    decimal MaxPoints,
    string? EvidenceBasis);
