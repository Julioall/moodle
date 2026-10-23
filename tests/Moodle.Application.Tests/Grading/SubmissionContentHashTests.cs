using Moodle.Application.Grading;

namespace Moodle.Application.Tests.Grading;

public sealed class SubmissionContentHashTests
{
    [Fact]
    public void Compute_IsDeterministicAndIndependentOfAttachmentOrder()
    {
        var modifiedAt = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var first = SubmissionContentHash.Compute(["bb", "aa"], "texto", 2, modifiedAt);
        var second = SubmissionContentHash.Compute(["aa", "bb"], "texto", 2, modifiedAt);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Compute_ChangesWhenReviewedSubmissionChanges()
    {
        var baseline = SubmissionContentHash.Compute(["aa"], "texto", 1, DateTimeOffset.UnixEpoch);
        var changed = SubmissionContentHash.Compute(["aa"], "texto revisado", 1, DateTimeOffset.UnixEpoch);

        Assert.NotEqual(baseline, changed);
    }
}
