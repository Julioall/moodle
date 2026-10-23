using Moodle.Application.Abstractions;
using Moodle.Application.Reports.Templates;
using Moodle.Domain;
using Moodle.Domain.Reports;

namespace Moodle.Application.Tests.Reports.Templates;

public sealed class ReportDatasetResolverTests
{
    [Fact]
    public async Task PreviewIsFairLimitedAndGradeBudgetIsGlobalAtRestTransport()
    {
        using var fixture = new ReportDatasetFixture();
        var result = await fixture.Resolver().ResolveAsync(fixture.Request(true, "student.name", "grade.value"), default);
        Assert.Equal(20, result.Table.Rows.Count);
        Assert.Equal(10, result.Table.Rows.Count(r => r.CourseId == "1"));
        Assert.Equal(10, result.Table.Rows.Count(r => r.CourseId == "2"));
        Assert.Equal(new[] { "1", "2" }, fixture.Courses.ReadIds);
        Assert.Equal(3, result.RequestedCourseCount);
        Assert.True(result.Partial);
        Assert.True(result.Truncated);
        Assert.Equal(2, fixture.Transport.Calls.Count(c => c.Function == "core_enrol_get_enrolled_users"));
        var gradeCalls = fixture.Transport.Calls.Where(c => c.Function == "gradereport_user_get_grade_items").ToArray();
        Assert.Equal(5, gradeCalls.Length);
        Assert.Equal(3, gradeCalls.Count(c => c.Parameters["courseid"] == "1"));
        Assert.Equal(2, gradeCalls.Count(c => c.Parameters["courseid"] == "2"));
        Assert.All(gradeCalls, c => Assert.NotEqual("0", c.Parameters["userid"]));
        Assert.Equal(15, result.Table.Rows.Count(r => r.Cells[1].Reason == "preview_read_budget"));
        Assert.Contains(result.Table.Coverage, c => c.Source == "unavailable" && c.Reason == "preview_read_budget");
    }

    [Fact]
    public async Task PreviewWithActivityFieldsMarksRowsUnavailableWithoutMutatingDuringEnumeration()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1, 2];
        var request = fixture.Request(true, "student.name", "activity.total", "tracking.situation") with
        {
            CourseIds = ["1"],
        };

        var result = await fixture.Resolver().ResolveAsync(request, default);

        Assert.Equal(2, result.Table.Rows.Count);
        Assert.All(result.Table.Rows, row => Assert.Equal(ReportCellState.Unavailable, row.Cells[1].State));
        Assert.All(result.Table.Rows, row => Assert.Equal("preview_read_budget", row.Cells[1].Reason));
        Assert.All(result.Table.Rows, row => Assert.Equal(ReportCellState.Unavailable, row.Cells[2].State));
        Assert.Contains("activity_preview_budget", result.Table.Warnings);
        Assert.Contains(result.Table.Coverage, item => item.Family == "activities" && item.Reason == "preview_read_budget");
    }

    [Fact]
    public async Task PreviewWithFewerPeopleThanTheRowLimitDoesNotMarkPopulationUnavailable()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = Enumerable.Range(1, 9).ToArray();
        var request = fixture.Request(true, "student.name", "activity.total") with { CourseIds = ["1"] };

        var result = await fixture.Resolver().ResolveAsync(request, default);

        Assert.Equal(9, result.Table.Rows.Count);
        Assert.DoesNotContain("population_unavailable", result.Table.Warnings);
        Assert.Contains("activity_preview_budget", result.Table.Warnings);
        Assert.Contains(result.Table.Coverage, item => item.Family == "population" && item.Source == "live" && item.Complete);
    }

    [Fact]
    public async Task ColdNamesJoinOnlyConfirmedLiveIdsAndOldAccessAndGradesCannotLeak()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        fixture.CourseAccess = 1710000000;
        var original = DateTimeOffset.UtcNow.AddMinutes(-5);
        fixture.Snapshots.Data[("students", "1")] = new MoodleSnapshotEnvelope<CourseParticipantsPage>(
            new("1", 1, 100, ParticipantStatusFilter.Active, true, false, false,
                [ReportDatasetFixture.Student("1", "Cold synthetic name"), ReportDatasetFixture.Student("2", "Removed synthetic")]),
            original, false, false, "warm", DateTimeOffset.UtcNow.AddMinutes(10));
        fixture.Snapshots.Data[("gradebook", "1")] = "Old grade must never be requested";
        var request = fixture.Request(false, "student.name", "access.course_at", "access.platform_at", "grade.value") with { CourseIds = ["1"] };
        var result = await fixture.Resolver().ResolveAsync(request, default);
        var row = Assert.Single(result.Table.Rows);
        Assert.Equal("1", row.StudentId);
        Assert.Equal("Cold synthetic name", row.Cells[0].Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710000000), row.Cells[1].Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), row.Cells[2].Value);
        Assert.Equal(25m, row.Cells[3].Value);
        Assert.DoesNotContain("gradebook", fixture.Snapshots.Reads);
        Assert.Contains(result.Table.Coverage, c => c.Source == "snapshot" && c.SnapshotAt == original && c.ReadAt is null);
        Assert.Contains(result.Table.Coverage, c => c.Source == "live" && c.FieldIds.Contains("access.course_at") && c.SnapshotAt is null && c.ReadAt > original);
        Assert.True(result.Table.GeneratedAt > original);
    }

    [Theory]
    [InlineData(false, false, false, "Cold synthetic")]
    [InlineData(true, false, false, "Cold synthetic")]
    [InlineData(false, true, false, "Live synthetic 1")]
    [InlineData(true, true, false, "Live synthetic 1")]
    [InlineData(false, false, true, "Live synthetic 1")]
    public async Task ExportColdEligibilityRequiresKnownFreshnessEvenWhenFrozen(bool frozen, bool expired, bool unknown, string expected)
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        fixture.Snapshots.Data[("students", "1")] = new MoodleSnapshotEnvelope<CourseParticipantsPage>(
            new("1", 1, 100, ParticipantStatusFilter.Active, true, false, false, [ReportDatasetFixture.Student("1", "Cold synthetic")]),
            DateTimeOffset.UtcNow.AddHours(-1), expired, frozen, "warm", unknown ? null
                : DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 10));
        var result = await fixture.Resolver().ResolveAsync(fixture.Request(false, "student.name") with { CourseIds = ["1"] }, default);
        Assert.Equal(expected, Assert.Single(result.Table.Rows).Cells[0].Value);
        Assert.DoesNotContain(fixture.Transport.Calls, c => c.Function.Contains("grade", StringComparison.Ordinal));
        Assert.Contains(fixture.Transport.Calls, c => c.Function == "core_enrol_get_enrolled_users");
    }

    [Fact]
    public async Task NewGenerationAndRegenerationReadHotValuesAgainAfterPreview()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        var request = fixture.Request(true, "grade.value") with { CourseIds = ["1"] };
        var resolver = fixture.Resolver();
        var preview = await resolver.ResolveAsync(request, default);
        fixture.GradeValue = 70;
        var generated = await resolver.ResolveAsync(request with { Preview = false }, default);
        fixture.GradeValue = 80;
        var regenerated = await resolver.ResolveAsync(request with { Preview = false }, default);
        Assert.Equal(25m, preview.Table.Rows[0].Cells[0].Value);
        Assert.Equal(70m, generated.Table.Rows[0].Cells[0].Value);
        Assert.Equal(80m, regenerated.Table.Rows[0].Cells[0].Value);
        Assert.Equal(3, fixture.Transport.Calls.Count(c => c.Function == "gradereport_user_get_grade_items"));
    }

    [Fact]
    public async Task LegacyScaleFieldsAreRemovedAndTotalGradeRemainsKnown()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        fixture.GradeMaxAvailable = false;
        var request = fixture.Request(true, "grade.value", "grade.max", "grade.percentage", "access.course_state", "access.course_at") with { CourseIds = ["1"] };
        var result = await fixture.Resolver().ResolveAsync(request, default);
        var cells = result.Table.Rows[0].Cells;
        Assert.Equal(25m, cells[0].Value);
        Assert.Equal(["grade.value", "access.never_course", "access.course_at"],
            result.Table.Columns.Select(column => column.FieldId));
        Assert.Equal("Sim", cells[1].Value);
        Assert.Equal(ReportCellState.Missing, cells[2].State);
        Assert.DoesNotContain(fixture.Transport.Calls, c => c.Function.StartsWith("mod_", StringComparison.Ordinal));
        fixture.FailGrade = true;
        var failure = await fixture.Resolver().ResolveAsync(request, default);
        Assert.Equal(ReportCellState.Unavailable, failure.Table.Rows[0].Cells[0].State);
        Assert.Contains("grade_unavailable", failure.Table.Warnings);
    }

    [Fact]
    public async Task FiltersCanUseAFieldWithoutAddingItToTheExportedColumns()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        var request = fixture.Request(false, "student.name") with
        {
            CourseIds = ["1"],
            Definition = new(1, "student_course", [new("student.name")],
                [new("grade.value", "greater_or_equal", "20")])
        };

        var result = await fixture.Resolver().ResolveAsync(request, default);

        Assert.Single(result.Table.Rows);
        Assert.Equal(["student.name"], result.Table.Columns.Select(column => column.FieldId));
        Assert.Equal("Live synthetic 1", result.Table.Rows[0].Cells[0].Value);
        Assert.Contains(fixture.Transport.Calls, call => call.Function == "gradereport_user_get_grade_items");
    }

    [Fact]
    public async Task NoGradeIsNotRenderedAsZero()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        fixture.GradeValue = null;
        var result = await fixture.Resolver().ResolveAsync(fixture.Request(false, "grade.value") with { CourseIds = ["1"] }, default);

        Assert.Equal(ReportCellState.Missing, result.Table.Rows[0].Cells[0].State);
    }

    [Fact]
    public async Task NeverAccessIsDistinctFromAnUnavailableDaysCount()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        var result = await fixture.Resolver().ResolveAsync(fixture.Request(true,
            "access.days_since_course", "access.never_course") with { CourseIds = ["1"] }, default);

        Assert.Equal(ReportCellState.Missing, result.Table.Rows[0].Cells[0].State);
        Assert.Equal("Sim", result.Table.Rows[0].Cells[1].Value);
    }

    [Fact]
    public async Task PopulationFailureAndRowLimitNeverProduceTruncatedExport()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Limits.MaxRows = 1;
        fixture.Population["1"] = [1, 2];
        var request = fixture.Request(false, "student.name") with { CourseIds = ["1"] };
        var limit = await Assert.ThrowsAsync<ReportDatasetException>(() => fixture.Resolver().ResolveAsync(request, default));
        Assert.Equal("report_row_limit", limit.Code);
        fixture.FailPopulation = true;
        var failure = await Assert.ThrowsAsync<ReportDatasetException>(() => fixture.Resolver().ResolveAsync(request, default));
        Assert.Equal("population_unavailable", failure.Code);
        Assert.DoesNotContain(fixture.Transport.Calls, c => c.Function.Contains("grade", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownParticipantRoleFailsExportAndMarksPreviewPartial()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1];
        fixture.ParticipantRoleShortName = "localrole";
        fixture.ParticipantRoleName = "Learner assistant";
        var export = fixture.Request(false, "student.name") with { CourseIds = ["1"] };

        var failure = await Assert.ThrowsAsync<ReportDatasetException>(() => fixture.Resolver().ResolveAsync(export, default));
        Assert.Equal("participant_classification_incomplete", failure.Code);

        var preview = await fixture.Resolver().ResolveAsync(export with { Preview = true }, default);
        Assert.True(preview.Partial);
        Assert.Contains("participant_classification_incomplete", preview.Table.Warnings);
        Assert.Contains(preview.Table.Coverage, item => item.UncertainCount == 1 && item.Reason == "participant_classification_incomplete");
    }

    [Fact]
    public async Task CourseLimitRejectsWholeSelectionBeforeReadingMoodle()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Limits.MaxCourses = 1;

        var failure = await Assert.ThrowsAsync<ReportDatasetException>(() =>
            fixture.Resolver().ResolveAsync(fixture.Request(false, "student.name"), default));

        Assert.Equal("report_course_limit", failure.Code);
        Assert.Empty(fixture.Transport.Calls);
    }

    [Fact]
    public async Task PreviewReusesCapacityWhenOneCourseHasFewerRowsAndEmptyPartialDoesNotMeanZero()
    {
        using var fixture = new ReportDatasetFixture();
        fixture.Population["1"] = [1, 2];
        var result = await fixture.Resolver().ResolveAsync(fixture.Request(true, "student.name"), default);
        Assert.Equal(20, result.Table.Rows.Count);
        Assert.Equal(18, result.Table.Rows.Count(r => r.CourseId == "2"));
        fixture.FailPopulation = true;
        var unavailable = await fixture.Resolver().ResolveAsync(fixture.Request(true, "student.name"), default);
        Assert.Empty(unavailable.Table.Rows);
        Assert.True(unavailable.Partial);
        Assert.Contains("sample_empty_partial", unavailable.Table.Warnings);
    }
}
