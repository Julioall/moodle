using Moodle.Domain;

namespace Moodle.Application.Tests.Domain;

public sealed class SubmissionEvaluationFeedbackEvidenceTests
{
    [Theory]
    [InlineData(null, null, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(0L, 0L, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(90L, 90L, SubmissionEvaluationState.AwaitingGrading)]
    [InlineData(100L, null, SubmissionEvaluationState.ReviewedWithFeedback)]
    [InlineData(null, 100L, SubmissionEvaluationState.ReviewedWithFeedback)]
    public void ReviewMustHaveCurrentPositiveTimestamp(
        long? reviewedAt, long? gradeModifiedAt, SubmissionEvaluationState expected)
    {
        var result = SubmissionEvaluationStateResolver.Resolve(new(
            HasSubmission: true, GradeRaw: null, GradedDateGraded: reviewedAt,
            Feedback: "Feedback sintético.", ReviewEvidenceAvailable: true,
            GraderId: 42, GradeTimeModified: gradeModifiedAt, SubmissionTimeModified: 100));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void FeedbackWithoutCompletedReview_RemainsAwaiting(long? reviewedAt)
    {
        var evidence = new SubmissionEvaluationEvidence(
            HasSubmission: true, GradeRaw: null, GradedDateGraded: reviewedAt,
            Feedback: "Comentário sintético ainda pendente de avaliação.",
            ReviewEvidenceAvailable: true, GradingStatus: "notgraded");

        var result = SubmissionEvaluationStateResolver.Resolve(evidence);

        Assert.Equal(SubmissionEvaluationState.AwaitingGrading, result);
        Assert.True(SubmissionEvaluationStateResolver.NeedsGrading(result));
    }

    [Fact]
    public void FeedbackWithPositiveReviewTimestamp_IsReviewed()
    {
        var result = SubmissionEvaluationStateResolver.Resolve(new(
            HasSubmission: true, GradeRaw: null, GradedDateGraded: 1700000000,
            Feedback: "Feedback sintético concluído.", ReviewEvidenceAvailable: true));

        Assert.Equal(SubmissionEvaluationState.ReviewedWithFeedback, result);
        Assert.False(SubmissionEvaluationStateResolver.NeedsGrading(result));
    }
}
