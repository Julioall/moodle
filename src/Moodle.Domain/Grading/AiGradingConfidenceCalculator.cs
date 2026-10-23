namespace Moodle.Domain.Grading;

public static class AiGradingConfidenceCalculator
{
    public static AiGradingConfidenceResult Calculate(
        decimal? modelConfidence,
        GradingEvidenceCoverage coverage,
        GradingExtractionSummary extraction,
        GradingScaleSnapshot? gradingScale,
        IReadOnlyList<AiGradingCriterionProposal>? criteria)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(extraction);

        var reasons = new List<string>();
        var model = Math.Clamp(modelConfidence ?? 0m, 0m, 1m);

        var coverageFactors = new List<decimal>();
        if (coverage.TotalArtifacts > 0)
        {
            coverageFactors.Add(Math.Clamp((decimal)coverage.IncludedArtifacts / coverage.TotalArtifacts, 0m, 1m));
        }

        if (coverage.TotalChunks > 0)
        {
            coverageFactors.Add(Math.Clamp((decimal)coverage.IncludedChunks / coverage.TotalChunks, 0m, 1m));
        }

        if (coverage.SourceCharacterCount > 0)
        {
            coverageFactors.Add(Math.Clamp((decimal)coverage.IncludedCharacterCount / coverage.SourceCharacterCount, 0m, 1m));
        }

        var coverageFactor = coverageFactors.Count == 0
            ? 0m
            : coverageFactors.Average();
        if (coverage.IsPartial || coverageFactor < 0.999m)
        {
            reasons.Add("evidence_coverage_partial");
        }

        var extractionFactor = extraction.Status switch
        {
            "succeeded" => 1m,
            "ocr_extracted" => 0.75m,
            "partial" => 0.5m,
            "scanned_pdf" => 0.35m,
            "file_too_large" => 0.25m,
            "empty" or "failed" or "unsupported_format" or "unsupported" => 0m,
            _ => 0.25m
        };
        if (extractionFactor < 1m || extraction.IsTruncated)
        {
            reasons.Add("extraction_requires_review");
        }

        var scaleFactor = gradingScale?.MaximumGrade is > 0 ? 1m : 0m;
        if (scaleFactor == 0m)
        {
            reasons.Add("grading_scale_unconfirmed");
        }

        var criteriaValues = criteria ?? [];
        var criterionFactors = criteriaValues.Count == 0
            ? [0m]
            : criteriaValues.Select(criterion => criterion.Source switch
            {
                AiGradingCriterionSource.FormalRubric or AiGradingCriterionSource.TeacherDefined => 1m,
                AiGradingCriterionSource.StatementDerived => 0.8m,
                AiGradingCriterionSource.GeneratedSupport when criterion.TeacherApproved => 0.7m,
                AiGradingCriterionSource.GeneratedSupport => 0m,
                _ => 0.25m
            }).ToArray();
        var criterionFactor = criteriaValues.Count == 0 ? 0m : criterionFactors.Average();
        if (criteriaValues.Count == 0)
        {
            reasons.Add("criteria_missing");
        }

        if (criteriaValues.Any(criterion =>
                criterion.Source == AiGradingCriterionSource.GeneratedSupport && !criterion.TeacherApproved))
        {
            reasons.Add("generated_criteria_not_approved");
        }

        var confidence = Math.Round(
            model * coverageFactor * extractionFactor * scaleFactor * criterionFactor,
            4,
            MidpointRounding.AwayFromZero);
        var reviewRequired = reasons.Count > 0 || confidence < 0.8m;
        if (confidence < 0.8m)
        {
            reasons.Add("confidence_below_review_threshold");
        }

        return new AiGradingConfidenceResult(
            Math.Clamp(confidence, 0m, 1m),
            reasons.Distinct(StringComparer.Ordinal).ToArray(),
            reviewRequired);
    }
}
