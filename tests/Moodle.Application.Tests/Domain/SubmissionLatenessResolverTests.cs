using Moodle.Domain;

namespace Moodle.Application.Tests.Domain;

public sealed class SubmissionLatenessResolverTests
{
    private static readonly DateTimeOffset Due = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-1, "on_time")]
    [InlineData(0, "on_time")]
    [InlineData(1, "late")]
    public void Zero_extension_uses_assignment_due_and_current_version(int offset, string expected)
    {
        var result = SubmissionLatenessResolver.Resolve("submitted", Due.AddSeconds(offset), Due.AddDays(-1), Due, true, null, true);
        Assert.Equal(expected, result.State);
        Assert.Equal(Due, result.EffectiveDueAt);
        Assert.Equal("moodle_submission_timemodified", result.TimestampSource);
    }

    [Fact]
    public void Extension_prevents_false_delay_and_timezone_does_not_change_comparison()
    {
        var result = SubmissionLatenessResolver.Resolve("submitted", Due.AddHours(1).ToOffset(TimeSpan.FromHours(-3)),
            Due.AddDays(-1), Due, true, Due.AddHours(2), true);
        Assert.Equal("on_time", result.State);
        Assert.Equal("individual_extension", result.DeadlineSource);
        Assert.Equal(Due.AddHours(2), result.EffectiveDueAt);
    }

    [Fact]
    public void Shorter_extension_never_reduces_normal_deadline()
    {
        var result = SubmissionLatenessResolver.Resolve("submitted", Due, null, Due, true, Due.AddHours(-1), true);
        Assert.Equal("on_time", result.State);
        Assert.Equal(Due, result.EffectiveDueAt);
    }

    [Theory]
    [InlineData(false, true, "assignment_deadline_unknown")]
    [InlineData(true, false, "individual_extension_unknown")]
    public void Missing_evidence_is_unknown(bool dueKnown, bool extensionKnown, string reason)
    {
        var result = SubmissionLatenessResolver.Resolve("submitted", Due.AddDays(1), null, Due, dueKnown, null, extensionKnown);
        Assert.Equal("unknown", result.State);
        Assert.Equal(reason, result.Reason);
        Assert.False(result.Late);
    }

    [Theory]
    [InlineData("draft", "not_submitted")]
    [InlineData("not_submitted", "not_submitted")]
    [InlineData("unexpected", "unknown")]
    public void Status_does_not_invent_delivery(string status, string expected) =>
        Assert.Equal(expected, SubmissionLatenessResolver.Resolve(status, Due, null, Due, false, null, false).State);

    [Fact]
    public void Known_zero_deadline_is_distinct_from_missing_deadline() =>
        Assert.Equal("no_deadline", SubmissionLatenessResolver.Resolve("submitted", Due, null, null, true, null, true).State);

    [Fact]
    public void Creation_is_not_a_submission_timestamp()
    {
        var result = SubmissionLatenessResolver.Resolve("submitted", null, Due.AddDays(-1), Due, true, null, true);
        Assert.Equal("unknown", result.State);
        Assert.Equal("creation_time_proxy", result.TimestampSource);
    }

    [Fact]
    public void Later_edit_changes_current_version_without_claiming_first_delivery_time()
    {
        var first = SubmissionLatenessResolver.Resolve("submitted", Due.AddMinutes(-1), Due.AddDays(-1), Due, true, null, true);
        var edited = SubmissionLatenessResolver.Resolve("submitted", Due.AddMinutes(1), Due.AddDays(-1), Due, true, null, true);
        Assert.Equal("on_time", first.State);
        Assert.Equal("late", edited.State);
        Assert.Equal("current_submission_version", edited.Reason);
    }
}
