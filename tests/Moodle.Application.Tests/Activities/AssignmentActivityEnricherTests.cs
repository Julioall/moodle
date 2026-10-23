using Moodle.Application.Abstractions;
using Moodle.Application.Activities;
using Moodle.Domain;

namespace Moodle.Application.Tests.Activities;

public sealed class AssignmentActivityEnricherTests
{
    [Theory]
    [InlineData(100, "numeric", 100, null)]
    [InlineData(-7, "scale", null, 7)]
    [InlineData(0, "feedback_only", null, null)]
    public async Task EnrichesAuthorizedAssignmentsInOneBatchAndKeepsScaleSemantics(int rawGrade, string mode, int? maximum, int? scaleId)
    {
        var settings = Definition("501", 11, rawGrade);
        var source = new Gateway([settings, Definition("502", 12, rawGrade), Definition("999", 99, 100)]);
        var original = Summary([Activity("11", "501"), Activity("12", "502")]);
        var result = await AssignmentActivityEnricher.EnrichAsync(original, source, "synthetic-teacher", CancellationToken.None);
        Assert.Equal(1, source.Calls);
        Assert.Equal(2, result.Total);
        Assert.Equal(new[] { "11", "12" }, result.Activities.Select(a => a.ActivityId));
        foreach (var activity in result.Activities)
        {
            Assert.Equal("<p>Official statement</p>", activity.Description);
            Assert.Equal(mode, activity.Assignment!.GradingMode);
            Assert.Equal(mode == "scale" ? "id_only" : mode == "numeric" ? "numeric_definition" : "not_applicable", activity.Assignment.ScaleCoverage);
            Assert.Equal(maximum is null ? null : (decimal?)maximum.Value, activity.Assignment.MaximumGrade);
            Assert.Equal(scaleId is null ? null : (long?)scaleId.Value, activity.Assignment.ScaleId);
            Assert.Equal("complete", activity.Assignment.Coverage);
            Assert.Single(activity.Assignment.IntroFiles!);
            Assert.Empty(activity.Assignment.IntroAttachments!);
            Assert.NotNull(activity.Assignment.ScaleVersion);
        }
        Assert.Contains(result.Warnings, w => w.Contains("fora dos módulos"));
        Assert.Equal("Generic description", original.Activities[0].Description);
    }

    [Fact]
    public async Task MissingOfficialDescriptionDoesNotEraseGenericTextOrInventGradingMode()
    {
        var definition = Definition("501", 11, 100) with { RawGrade = null, IsGradable = null, Description = null, DescriptionPresent = false, IntroFiles = null, IntroFilesComplete = null };
        var result = await AssignmentActivityEnricher.EnrichAsync(Summary([Activity("11", "501")]), new Gateway([definition]), "synthetic", CancellationToken.None);
        Assert.Equal("Generic description", result.Activities[0].Description);
        Assert.Equal("unknown", result.Activities[0].Assignment!.GradingMode);
        Assert.Equal("partial", result.Activities[0].Assignment!.Coverage);
        Assert.Null(result.Activities[0].Assignment!.MaximumGrade);
    }

    [Fact]
    public async Task OfficialZeroDueDateOverridesGenericDueWithoutUsingCutoffAsDue()
    {
        var definition = Definition("501", 11, 100) with { DueAt = null, DueAtKnown = true, CutoffAt = DateTimeOffset.UnixEpoch.AddDays(3) };
        var result = await AssignmentActivityEnricher.EnrichAsync(Summary([Activity("11", "501") with { DueAt = DateTimeOffset.UnixEpoch.AddDays(2), DueAtKnown = true }]),
            new Gateway([definition]), "synthetic", CancellationToken.None);
        Assert.Null(result.Activities[0].DueAt);
        Assert.True(result.Activities[0].DueAtKnown);
        Assert.Equal(definition.CutoffAt, result.Activities[0].CutoffAt);
        Assert.Equal("official", result.Activities[0].DateCoverage);
        Assert.True(result.Activities[0].HasDeadline);
    }

    [Fact]
    public async Task SourceFailureOrMismatchPreservesGenericDataWithExplicitCoverage()
    {
        var original = Summary([Activity("11", "501")]);
        foreach (var source in new[] { new Gateway([]) { Fail = true }, new Gateway([Definition("501", 12, 100)]) })
        {
            var result = await AssignmentActivityEnricher.EnrichAsync(original, source, "synthetic", CancellationToken.None);
            Assert.Equal("Generic description", result.Activities[0].Description);
            Assert.Contains(result.Activities[0].Assignment!.Coverage, new[] { "unavailable", "not_matched" });
            Assert.NotEmpty(result.Warnings);
        }
    }

    [Fact]
    public async Task CancellationIsPropagatedAndNonAssignmentsDoNotQuerySettings()
    {
        var source = new Gateway([]) { Cancel = true };
        await Assert.ThrowsAsync<OperationCanceledException>(() => AssignmentActivityEnricher.EnrichAsync(Summary([Activity("11", "501")]), source, "synthetic", CancellationToken.None));
        var quiz = Summary([Activity("11", "501") with { ActivityType = "quiz" }]);
        Assert.Same(quiz, await AssignmentActivityEnricher.EnrichAsync(quiz, source, "synthetic", CancellationToken.None));
        Assert.Equal(1, source.Calls);
    }

    private static AssignmentSettingsSummary Definition(string id, long moduleId, int grade) => new(id, Math.Max(0, grade), RawGrade: grade,
        GradeScaleId: grade < 0 ? -grade : null, Description: "<p>Official statement</p>", IntroAttachments: [], IntroAttachmentsComplete: true)
    {
        CourseModuleId = moduleId, DescriptionPresent = true, SourceConnectionId = "synthetic-connection",
        IntroFiles = [new CourseModuleFile("file", "statement.pdf", "/", 3, "application/pdf", "https://moodle.example/pluginfile.php/1/statement.pdf", false)],
        IntroFilesComplete = true, OpenAtKnown = true, DueAtKnown = true, CutoffAtKnown = true
    };
    private static CourseActivitySummary Activity(string id, string instance) => new(id, instance, "assign", "Synthetic", null, true, true, "Generic description", null, false, false, null, null, null, [], 0);
    private static CourseActivitiesSummary Summary(CourseActivitySummary[] activities) => new("101", ["assign"], false, activities.Length, activities.Length, activities.Length, activities);
    private sealed class Gateway(AssignmentSettingsSummary[] settings) : IMoodleAssignmentSettingsGateway
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public bool Cancel { get; init; }
        public Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsAsync(string userExternalId, string courseId, CancellationToken cancellationToken)
        {
            Calls++;
            if (Cancel) throw new OperationCanceledException();
            if (Fail) throw new HttpRequestException("Synthetic failure");
            return Task.FromResult<IReadOnlyDictionary<string, AssignmentSettingsSummary>>(settings.ToDictionary(s => s.AssignmentId));
        }
        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(string userExternalId, string courseId, string assignmentId, CancellationToken cancellationToken) => throw new InvalidOperationException("Individual reads are not allowed in this projection.");
    }
}
