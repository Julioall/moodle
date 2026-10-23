using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

/// <summary>
/// Converts points from an exactly associated active Moodle rubric to the
/// activity grade only when Moodle's zero-origin linear mapping is verified.
/// </summary>
internal static class MoodleRubricScoreNormalizer
{
    public const string RuleVersion = "moodle-rubric-linear-zero-origin-v1-4dp-away-from-zero";
    private const decimal MaximumPersistableGrade = 999_999.9999m;

    public static GradingScoreNormalizationSnapshot Normalize(
        GradingRubricSnapshot? rubric,
        GradingScaleSnapshot? scale,
        IReadOnlyList<AiGradingCriterionProposal> criteria)
    {
        if (rubric is null ||
            !string.Equals(rubric.AssociationStatus, "active", StringComparison.Ordinal) ||
            !string.Equals(rubric.Method, "rubric", StringComparison.Ordinal) ||
            !string.Equals(rubric.AreaName, "submissions", StringComparison.Ordinal) ||
            rubric.CourseModuleId is not > 0 ||
            rubric.DefinitionId is not > 0 ||
            string.IsNullOrWhiteSpace(rubric.DefinitionVersion))
        {
            throw new InvalidOperationException("A conversao da rubrica foi bloqueada: nao ha associacao ativa verificavel no Moodle.");
        }

        if (scale is null ||
            !string.Equals(scale.GradingMode, "numeric", StringComparison.Ordinal) ||
            scale.MinimumGrade != 0m ||
            scale.MaximumGrade is not > 0)
        {
            throw new InvalidOperationException("A conversao da rubrica exige escala numerica Moodle confirmada de zero ate um maximo positivo.");
        }

        var officialCriteria = rubric.Criteria ?? [];
        if (officialCriteria.Count == 0 ||
            rubric.MinimumPoints != 0m ||
            rubric.MaximumPoints is not > 0 ||
            officialCriteria.Any(criterion => criterion.MinimumPoints != 0m))
        {
            throw new InvalidOperationException("A conversao foi bloqueada: a rubrica precisa ter origem zero e maximo positivo.");
        }

        if (criteria.Count != officialCriteria.Count ||
            criteria.Any(criterion =>
                !string.Equals(
                    AiGradingCriterionSource.Normalize(criterion.Source),
                    AiGradingCriterionSource.FormalRubric,
                    StringComparison.Ordinal)) ||
            criteria.Select(criterion => criterion.CriterionId).Distinct(StringComparer.Ordinal).Count() != criteria.Count)
        {
            throw new InvalidOperationException("A conversao foi bloqueada: a proposta nao cobre exatamente os criterios da rubrica ativa.");
        }

        var proposedById = criteria.ToDictionary(criterion => criterion.CriterionId, StringComparer.Ordinal);
        if (officialCriteria.Select(criterion => criterion.CriterionId).Distinct(StringComparer.Ordinal).Count() != officialCriteria.Count)
        {
            throw new InvalidOperationException("A conversao foi bloqueada: a definicao ativa contem criterios duplicados.");
        }

        decimal rawRubricScore = 0m;
        foreach (var officialCriterion in officialCriteria)
        {
            if (!proposedById.TryGetValue(officialCriterion.CriterionId, out var proposal) ||
                proposal.SuggestedPoints is not decimal points ||
                proposal.MaxPoints != officialCriterion.MaximumPoints ||
                !officialCriterion.Levels.Any(level => level.Score == points))
            {
                throw new InvalidOperationException("A conversao foi bloqueada: um criterio nao corresponde aos pontos de nivel oficiais da rubrica.");
            }

            rawRubricScore += points;
        }

        if (rawRubricScore < rubric.MinimumPoints || rawRubricScore > rubric.MaximumPoints)
        {
            throw new InvalidOperationException("A conversao foi bloqueada: a soma da rubrica esta fora da faixa oficial.");
        }

        var maximumGrade = scale.MaximumGrade.Value;
        var unrounded = rawRubricScore * maximumGrade / rubric.MaximumPoints.Value;
        if (unrounded < scale.MinimumGrade.Value || unrounded > maximumGrade)
        {
            throw new InvalidOperationException("A conversao foi bloqueada: a nota normalizada esta fora da faixa Moodle.");
        }

        // grading_item.SuggestedGrade and FinalGrade are numeric(10,4). Round
        // once at that persistence boundary; the factor's exact numerator and
        // denominator remain available for audit.
        var normalized = decimal.Round(unrounded, 4, MidpointRounding.AwayFromZero);
        if (normalized > MaximumPersistableGrade)
        {
            throw new InvalidOperationException("A conversao foi bloqueada: a nota excede a precisao persistida pelo conector.");
        }

        return new GradingScoreNormalizationSnapshot(
            rawRubricScore,
            rubric.MinimumPoints.Value,
            rubric.MaximumPoints.Value,
            scale.MinimumGrade.Value,
            maximumGrade,
            rubric.MaximumPoints.Value != maximumGrade,
            maximumGrade,
            rubric.MaximumPoints.Value,
            maximumGrade / rubric.MaximumPoints.Value,
            unrounded,
            normalized,
            RuleVersion);
    }
}
