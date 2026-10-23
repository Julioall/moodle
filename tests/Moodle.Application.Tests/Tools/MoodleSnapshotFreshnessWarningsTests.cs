using Moodle.Presentation.Tools;

namespace Moodle.Application.Tests.Tools;

public sealed class MoodleSnapshotFreshnessWarningsTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void DecisionSafe_exige_snapshot_completo_e_nao_stale(bool complete, bool stale, bool expected)
    {
        Assert.Equal(expected, MoodleSnapshotFreshnessWarnings.IsDecisionSafe(complete, stale));
    }
}
