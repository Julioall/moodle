namespace Moodle.Domain;

/// <summary>Canonical state derived from submission and grading evidence.</summary>
public enum SubmissionEvaluationState
{
    Unknown = 0,
    NotSubmitted = 1,
    AwaitingGrading = 2,
    ReviewedWithFeedback = 3,
    GradedNumeric = 4
}

public sealed record SubmissionEvaluationEvidence(
    bool? HasSubmission,
    decimal? GradeRaw,
    long? GradedDateGraded,
    string? Feedback,
    bool ReviewEvidenceAvailable,
    string? GradingStatus = null,
    long? GraderId = null,
    long? GradeTimeModified = null,
    long? SubmissionTimeModified = null);

public static class SubmissionEvaluationStateResolver
{
    public static SubmissionEvaluationState Resolve(SubmissionEvaluationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.HasSubmission == false)
        {
            return SubmissionEvaluationState.NotSubmitted;
        }

        if (evidence.HasSubmission is null)
        {
            return SubmissionEvaluationState.Unknown;
        }

        if (evidence.GradeRaw.HasValue)
        {
            return SubmissionEvaluationState.GradedNumeric;
        }

        // Feedback may be a draft or an earlier comment. It does not prove that
        // this submission was evaluated. Moodle's zero timestamp is absent.
        if (IsCurrentReview(evidence.GradedDateGraded, evidence.SubmissionTimeModified))
        {
            return SubmissionEvaluationState.ReviewedWithFeedback;
        }

        if (string.Equals(evidence.GradingStatus?.Trim(), "graded", StringComparison.OrdinalIgnoreCase))
        {
            return SubmissionEvaluationState.ReviewedWithFeedback;
        }

        if (evidence.GraderId is > 0 &&
            IsCurrentReview(evidence.GradeTimeModified, evidence.SubmissionTimeModified))
        {
            return SubmissionEvaluationState.ReviewedWithFeedback;
        }

        // Only a successful, sufficiently complete review read lets absence of
        // grade/review evidence mean that correction is still pending. If the
        // read failed or coverage is incomplete, absence is not evidence.
        if (!evidence.ReviewEvidenceAvailable)
        {
            return SubmissionEvaluationState.Unknown;
        }

        return SubmissionEvaluationState.AwaitingGrading;
    }

    public static bool NeedsGrading(SubmissionEvaluationState state) =>
        state == SubmissionEvaluationState.AwaitingGrading;

    private static bool IsCurrentReview(long? reviewedAt, long? submittedAt) =>
        reviewedAt is > 0 && (submittedAt is null or <= 0 || reviewedAt >= submittedAt);
}
