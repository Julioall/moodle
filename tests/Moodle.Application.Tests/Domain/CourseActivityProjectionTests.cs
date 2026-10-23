using Moodle.Domain;

namespace Moodle.Application.Tests.Domain;

public sealed class CourseActivityProjectionTests
{
    [Fact]
    public void MapsAccentedLocalizedDatesAndKeepsCutoffSeparateFromDue()
    {
        var open = DateTimeOffset.UnixEpoch.AddDays(1);
        var due = open.AddDays(1);
        var cutoff = due.AddDays(1);
        var close = cutoff.AddDays(1);
        var module = new CourseModuleSummary("11", "501", "assign", "Synthetic", null, true, true, null, null,
            [new("Disponível a partir de", open), new("Vencimento", due), new("Cut-off", cutoff), new("Disponível até", close)], []);
        var activity = CourseActivityProjection.FromModule(module);
        Assert.Equal(open, activity.OpenAt);
        Assert.Equal(due, activity.DueAt);
        Assert.Equal(cutoff, activity.CutoffAt);
        Assert.Equal(close, activity.CloseAt);
        Assert.Equal("mapped", activity.DateCoverage);
        var onlyCutoff = CourseActivityProjection.FromModule(module with { Dates = [new("Cutoff", cutoff)] });
        Assert.Null(onlyCutoff.DueAt);
        Assert.False(onlyCutoff.DueAtKnown);
        Assert.True(onlyCutoff.HasDeadline);
        var unknown = CourseActivityProjection.FromModule(module with { Dates = [new("unrecognized label", due)] });
        Assert.Equal("unmapped", unknown.DateCoverage);
        Assert.True(unknown.HasDates);
        Assert.Single(unknown.Dates);
    }
}
