using System.Text.Json.Serialization;
using Moodle.Application.MoodleApi;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Entrada estruturada opcional para substituir o contrato legado de nota/feedback.
/// A escala numérica continua sendo resolvida exclusivamente a partir do Moodle.
/// </summary>
public sealed record AiGradingProposalInput(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("contextHash")] string? ContextHash,
    [property: JsonPropertyName("suggestedGrade")] decimal? SuggestedGrade,
    [property: JsonPropertyName("feedback")] string? Feedback,
    [property: JsonPropertyName("criteria")] IReadOnlyList<AiGradingCriterionInput>? Criteria,
    [property: JsonPropertyName("evidence")] IReadOnlyList<AiGradingEvidenceInput>? Evidence,
    [property: JsonPropertyName("resourceUris")] IReadOnlyList<string>? ResourceUris,
    [property: JsonPropertyName("gaps")] IReadOnlyList<string>? Gaps,
    [property: JsonPropertyName("modelConfidence")] decimal? ModelConfidence,
    [property: JsonPropertyName("extraction")] GradingExtractionSummary? Extraction,
    [property: JsonPropertyName("coverage")] GradingEvidenceCoverage? Coverage,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("submissionContentHash")] string? SubmissionContentHash = null);

public sealed record AiGradingCriterionInput(
    [property: JsonPropertyName("criterionId")] string CriterionId,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("maxPoints")] decimal? MaxPoints,
    [property: JsonPropertyName("suggestedPoints")] decimal? SuggestedPoints,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("evidenceText")] string? EvidenceText,
    [property: JsonPropertyName("gapsText")] string? GapsText,
    [property: JsonPropertyName("teacherReviewRequired")] bool TeacherReviewRequired,
    [property: JsonPropertyName("teacherApproved")] bool TeacherApproved,
    [property: JsonPropertyName("artifactIds")] IReadOnlyList<Guid>? ArtifactIds);

public sealed record AiGradingEvidenceInput(
    [property: JsonPropertyName("artifactId")] Guid ArtifactId,
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("quoteHash")] string? QuoteHash,
    [property: JsonPropertyName("resourceUri")] string? ResourceUri = null);

public static class AiGradingProposalFactory
{
    public static AiGradingProposal Create(
        AssistedGradingItem item,
        AiGradingProposalInput input,
        decimal? authoritativeMaxGrade,
        int version,
        string? fallbackFeedback = null,
        string? submissionContentHash = null,
        GradingContextSnapshot? contextSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(input);

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        var itemContextHash = string.IsNullOrWhiteSpace(item.ContextHash)
            ? null
            : item.ContextHash.Trim();
        var proposalContextHash = string.IsNullOrWhiteSpace(input.ContextHash)
            ? null
            : input.ContextHash.Trim();
        if (!string.Equals(itemContextHash, proposalContextHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A proposta nao corresponde ao contexto versionado do item.");
        }

        if (contextSnapshot is not null &&
            (contextSnapshot.ItemId != item.Id ||
             !string.Equals(contextSnapshot.ContextHash, itemContextHash, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("O snapshot nao corresponde ao contexto versionado do item.");
        }

        var snapshotScale = contextSnapshot?.GradingScale;
        var scale = authoritativeMaxGrade is > 0
            ? snapshotScale is { GradingMode: "numeric" } && snapshotScale.MaximumGrade == authoritativeMaxGrade
                ? snapshotScale
                : new GradingScaleSnapshot(authoritativeMaxGrade, "points", "Moodle", "numeric", 0m)
            : null;
        var extraction = input.Extraction ?? new GradingExtractionSummary("missing", 0, false, 0, 0, null);
        var coverage = input.Coverage ?? new GradingEvidenceCoverage(0, 0, 0, 0, 0, 0, true);
        var criteria = (input.Criteria ?? [])
            .Select(criterion => new AiGradingCriterionProposal(
                criterion.CriterionId,
                criterion.Description,
                criterion.MaxPoints,
                criterion.SuggestedPoints,
                criterion.Source,
                criterion.EvidenceText,
                criterion.GapsText,
                criterion.TeacherReviewRequired,
                criterion.TeacherApproved,
                criterion.ArtifactIds ?? []))
            .ToArray();
        var evidence = (input.Evidence ?? [])
            .Select(value =>
            {
                if (!string.IsNullOrWhiteSpace(value.ResourceUri) && !MoodleResourceUri.TryParse(value.ResourceUri, out _))
                    throw new InvalidOperationException("A evidencia aponta para uma URI de resource invalida.");
                return new AiGradingEvidenceReference(value.ArtifactId, value.Reference, value.QuoteHash, value.ResourceUri);
            })
            .ToArray();
        var resourceUris = (input.ResourceUris ?? [])
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Select(uri =>
            {
                if (!MoodleResourceUri.TryParse(uri, out _))
                    throw new InvalidOperationException("A proposta aponta para uma URI de resource invalida.");
                return uri.Trim();
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var confidence = AiGradingConfidenceCalculator.Calculate(
            input.ModelConfidence,
            coverage,
            extraction,
            scale,
            criteria);
        var feedback = string.IsNullOrWhiteSpace(input.Feedback)
            ? (string.IsNullOrWhiteSpace(fallbackFeedback) ? item.DraftFeedback : fallbackFeedback)
            : input.Feedback;
        var grade = input.SuggestedGrade;
        GradingScoreNormalizationSnapshot? scoreNormalization = null;
        var containsFormalRubricPoints = criteria.Any(criterion =>
            string.Equals(
                AiGradingCriterionSource.Normalize(criterion.Source),
                AiGradingCriterionSource.FormalRubric,
                StringComparison.Ordinal));
        if (containsFormalRubricPoints)
        {
            scoreNormalization = MoodleRubricScoreNormalizer.Normalize(
                contextSnapshot?.Rubric,
                scale,
                criteria);
            if (input.SuggestedGrade is decimal submittedGrade &&
                submittedGrade != scoreNormalization.RawRubricScore &&
                submittedGrade != scoreNormalization.NormalizedMoodleScore)
            {
                throw new InvalidOperationException("A nota da proposta diverge tanto da pontuacao bruta da rubrica quanto da nota Moodle normalizada.");
            }

            grade = scoreNormalization.NormalizedMoodleScore;
        }

        if (grade is not null && authoritativeMaxGrade is not > 0)
        {
            throw new InvalidOperationException("Uma nota sugerida exige escala numerica confirmada pelo Moodle.");
        }

        return AiGradingProposal.Create(
            item.Id,
            item.BatchId,
            version,
            itemContextHash,
            grade,
            feedback,
            criteria,
            evidence,
            input.Gaps,
            scale,
            extraction,
            coverage,
            confidence,
            reviewRequired: true,
            status: string.IsNullOrWhiteSpace(input.Status) ? "ready_for_review" : input.Status!,
            submissionContentHash: submissionContentHash,
            resourceUris: resourceUris,
            scoreNormalization: scoreNormalization);
    }
}
