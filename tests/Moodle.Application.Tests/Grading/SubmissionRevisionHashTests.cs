using Moodle.Application.Grading;
using Moodle.Domain;

namespace Moodle.Application.Tests.Grading;

public sealed class SubmissionRevisionHashTests
{
    [Fact]
    public void Compute_IsStableWhenOnlyTheEphemeralFileUrlChanges()
    {
        var first = CreateSubmission("https://moodle.example/pluginfile.php/first");
        var second = first with
        {
            Files = [first.Files![0] with { FileUrl = "https://moodle.example/pluginfile.php/second?token=rotated" }]
        };

        Assert.Equal(SubmissionRevisionHash.Compute(first), SubmissionRevisionHash.Compute(second));
    }

    [Fact]
    public void Compute_ChangesWhenTheSubmissionMetadataChanges()
    {
        var first = CreateSubmission("https://moodle.example/pluginfile.php/first");
        var changed = first with
        {
            ModifiedAt = first.ModifiedAt!.Value.AddSeconds(1),
            Files = [first.Files![0] with { SizeBytes = 13 }]
        };

        Assert.NotEqual(SubmissionRevisionHash.Compute(first), SubmissionRevisionHash.Compute(changed));
    }

    [Fact]
    public void TryCompute_RejectsIncompleteFileMetadata()
    {
        var incomplete = CreateSubmission(null) with
        {
            Files = [new AssignmentSubmissionFile("submission.pdf", "application/pdf", null, "")]
        };

        Assert.False(SubmissionRevisionHash.TryCompute(incomplete, 100, out string? revision));
        Assert.Null(revision);
    }

    private static AssignmentSubmissionRecord CreateSubmission(string? fileUrl) => new(
        SubmissionId: "777",
        UserId: "101",
        Status: "submitted",
        GradingStatus: null,
        CreatedAt: DateTimeOffset.Parse("2026-09-23T17:00:00Z"),
        ModifiedAt: DateTimeOffset.Parse("2026-09-23T17:00:00Z"),
        AttemptNumber: 2,
        FileCount: 1,
        HasOnlineText: false,
        Files: [new AssignmentSubmissionFile("submission.pdf", "application/pdf", 12, fileUrl ?? "")]);
}
