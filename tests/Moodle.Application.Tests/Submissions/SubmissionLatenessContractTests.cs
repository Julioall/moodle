using Moodle.Domain;
using Moodle.Presentation;

namespace Moodle.Application.Tests.Submissions;

public sealed class SubmissionLatenessContractTests
{
    [Fact]
    public void Portal_mapper_preserves_canonical_state_and_coverage()
    {
        var row = new AssignmentSubmissionSummary("student-1", "Estudante sintético", "submission-1",
            "submitted", null, true, true, false, null, null, 1, 0, false);
        var legacyDto = AppSubmissionContractMapper.ToDto(row);
        Assert.False(legacyDto.Late);
        Assert.Equal("unknown", legacyDto.LatenessState);
        var due = DateTimeOffset.FromUnixTimeSeconds(1000);
        row = SubmissionLatenessResolver.Apply(row, SubmissionLatenessResolver.Resolve(
            "submitted", due.AddSeconds(1), null, due, true, null, true));
        var page = new AssignmentSubmissionsPage("course-1", "assignment-1", "module-1", "Tarefa sintética",
            1, 20, AssignmentSubmissionFilter.All, true, true, null, null, 1, false, [row])
        {
            LatenessUnknownCount = 2,
            LatenessCoverageComplete = true,
            EvaluationUnknownCount = 1,
            EvaluationCoverageComplete = false
        };

        var dto = AppSubmissionContractMapper.ToPage(page);
        var item = Assert.Single(dto.Submissions);
        Assert.True(item.Late);
        Assert.Equal("late", item.LatenessState);
        Assert.Equal(due, item.EffectiveDueAt);
        Assert.Equal(row.SubmissionTimestampSource, item.SubmissionTimestampSource);
        Assert.Equal(2, dto.LatenessUnknownCount);
        Assert.True(dto.LatenessCoverageComplete);
        Assert.Equal(1, dto.EvaluationUnknownCount);
        Assert.False(dto.EvaluationCoverageComplete);
    }
}
