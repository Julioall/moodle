using Moodle.Domain;

namespace Moodle.Application.Tests.Submissions;

public sealed class SubmissionEvaluationStateResolverTests
{
    [Theory]
    [InlineData(false, null, null, null, null, false, SubmissionEvaluationState.NotSubmitted)]
    [InlineData(null, null, null, null, null, true, SubmissionEvaluationState.Unknown)]
    [InlineData(true, 0, null, null, null, false, SubmissionEvaluationState.GradedNumeric)]
    [InlineData(true, null, null, "graded", null, false, SubmissionEvaluationState.ReviewedWithFeedback)]
    [InlineData(true, null, null, "notgraded", null, true, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(true, null, null, null, null, true, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(true, null, null, null, "feedback only", true, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(true, null, null, "notgraded", null, false, SubmissionEvaluationState.Unknown)]
    [InlineData(true, null, null, null, "feedback only", false, SubmissionEvaluationState.Unknown)]
    public void Resolve_applies_the_grading_and_coverage_matrix(
        bool? hasSubmission,
        object? gradeRaw,
        long? gradedAt,
        string? gradingStatus,
        string? feedback,
        bool reviewEvidenceAvailable,
        SubmissionEvaluationState expected)
    {
        var actual = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            hasSubmission,
            gradeRaw is null ? null : Convert.ToDecimal(gradeRaw),
            gradedAt,
            feedback,
            reviewEvidenceAvailable,
            gradingStatus));

        Assert.Equal(expected, actual);
        Assert.Equal(expected == SubmissionEvaluationState.AwaitingGrading,
            SubmissionEvaluationStateResolver.NeedsGrading(actual));
    }

    [Theory]
    [InlineData(0L, true, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(-1L, true, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(90L, true, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(100L, false, SubmissionEvaluationState.ReviewedWithFeedback)]
    public void Review_timestamp_must_be_positive_and_current(
        long? reviewedAt,
        bool reviewEvidenceAvailable,
        SubmissionEvaluationState expected)
    {
        var actual = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            HasSubmission: true,
            GradeRaw: null,
            GradedDateGraded: reviewedAt,
            Feedback: null,
            ReviewEvidenceAvailable: reviewEvidenceAvailable,
            SubmissionTimeModified: 100));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Grader_without_current_timestamp_does_not_complete_review()
    {
        var actual = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            HasSubmission: true,
            GradeRaw: null,
            GradedDateGraded: null,
            Feedback: "comentário sintético",
            ReviewEvidenceAvailable: true,
            GraderId: 42,
            GradeTimeModified: 0,
            SubmissionTimeModified: 100));

        Assert.Equal(SubmissionEvaluationState.AwaitingGrading, actual);
    }

    [Theory]
    [InlineData(SubmissionEvaluationState.Unknown)]
    [InlineData(SubmissionEvaluationState.NotSubmitted)]
    [InlineData(SubmissionEvaluationState.ReviewedWithFeedback)]
    [InlineData(SubmissionEvaluationState.GradedNumeric)]
    public void NeedsGrading_is_false_for_non_pending_states(SubmissionEvaluationState state)
    {
        Assert.False(SubmissionEvaluationStateResolver.NeedsGrading(state));
    }

    [Fact]
    public void Resolve_rejects_null_evidence()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SubmissionEvaluationStateResolver.Resolve(null!));
    }
}
