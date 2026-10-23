using Moodle.Domain;

namespace Moodle.Application.Tests.Domain;

public sealed class SubmissionEvaluationStateResolverTests
{
    [Theory]
    [InlineData("submitted", true)]
    [InlineData("draft", false)]
    [InlineData("notsubmitted", false)]
    [InlineData("unknown", null)]
    public void Submission_presence_is_independent_of_grading_coverage(
        string? status,
        bool? expectedPresence)
    {
        bool? hasSubmission = status?.Trim().ToLowerInvariant() switch
        {
            "submitted" => true,
            "draft" or "notsubmitted" => false,
            _ => null
        };
        var result = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            hasSubmission,
            GradeRaw: null,
            GradedDateGraded: null,
            Feedback: null,
            ReviewEvidenceAvailable: false,
            GradingStatus: "notgraded"));

        var expected = expectedPresence switch
        {
            false => SubmissionEvaluationState.NotSubmitted,
            true => SubmissionEvaluationState.Unknown,
            null => SubmissionEvaluationState.Unknown
        };
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Graded_status_is_positive_review_evidence_even_without_gradebook_coverage()
    {
        var result = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            HasSubmission: true,
            GradeRaw: null,
            GradedDateGraded: null,
            Feedback: null,
            ReviewEvidenceAvailable: false,
            GradingStatus: "graded"));

        Assert.Equal(SubmissionEvaluationState.ReviewedWithFeedback, result);
        Assert.False(SubmissionEvaluationStateResolver.NeedsGrading(result));
    }

    [Fact]
    public void Feedback_text_alone_does_not_complete_review()
    {
        var result = SubmissionEvaluationStateResolver.Resolve(new SubmissionEvaluationEvidence(
            HasSubmission: true,
            GradeRaw: null,
            GradedDateGraded: null,
            Feedback: "feedback sintético",
            ReviewEvidenceAvailable: true));

        Assert.Equal(SubmissionEvaluationState.AwaitingGrading, result);
        Assert.True(SubmissionEvaluationStateResolver.NeedsGrading(result));
    }
}
