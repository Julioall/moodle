using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Gradebook.Queries;
using Moodle.Application.Reports.Queries;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Reports;

public sealed class DerivedGradeReportsQueryHandlerTests
{
    [Fact]
    public async Task PostExecution_orders_outcomes_and_counts_aggregate_only_as_unknown()
    {
        var now = DateTimeOffset.UtcNow;
        var students = new[] { "complete", "aggregate", "recovery", "risk" }
            .Select(id => new CourseParticipantSummary(id, "Estudante sintético " + id,
                null, false, null, now, id == "risk" ? null : now, [], [],
                LastCourseAccessEvidence: id == "risk"
                    ? ParticipantAccessEvidence.Never
                    : ParticipantAccessEvidence.Observed))
            .ToArray();
        var scenarios = new Dictionary<string, string>
        {
            ["complete"] = "all_pass", ["aggregate"] = "total_only",
            ["recovery"] = "activity_low", ["risk"] = "both_low"
        };
        var result = await new GeneratePostExecutionReportQueryHandler(
            new FakeParticipantsGateway(students: students),
            new FakeGradebookGateway(scenarios: scenarios), new FakeCurrentUserGateway(),
            new SubmissionMediator("complete", ["complete", "aggregate", "recovery", "risk"]))
            .Handle(new GeneratePostExecutionReportQuery("10"), CancellationToken.None);

        Assert.Equal(new[] { "risk", "recovery", "aggregate", "complete" },
            result.Students.Select(row => row.StudentId));
        Assert.Equal(1, result.AtRisk);
        Assert.Equal(1, result.PendingRecovery);
        Assert.Equal(1, result.Unknown);
        Assert.Equal(1, result.LikelyComplete);
        Assert.Equal(0, result.Students.Single(row => row.StudentId == "aggregate").BelowMinimumCount);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("failure")]
    [InlineData("roster_truncated")]
    public async Task PostExecution_incomplete_sources_prevent_positive_outcome(string mode)
    {
        var result = await new GeneratePostExecutionReportQueryHandler(
            new FakeParticipantsGateway(mode == "roster_truncated"),
            new FakeGradebookGateway(ScenarioItems("all_pass")), new FakeCurrentUserGateway(),
            new SubmissionMediator(mode))
            .Handle(new GeneratePostExecutionReportQuery("10"), CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.Equal("unknown", row.OutcomeIndicator);
        Assert.Equal("incomplete_activity_evidence", row.OutcomeReason);
        Assert.Equal(0, result.LikelyComplete);
    }

    [Theory]
    [InlineData("total_only", "unknown", "aggregate_grade_risk", 0)]
    [InlineData("high_total_only", "unknown", "no_activity_evidence", 0)]
    [InlineData("aggregate_low", "unknown", "aggregate_grade_risk", 0)]
    [InlineData("activity_low", "pending_recovery", null, 1)]
    [InlineData("both_low", "pending_recovery", null, 1)]
    [InlineData("awaiting", "unknown", "awaiting_grading", 0)]
    [InlineData("all_pass", "likely_complete", null, 0)]
    [InlineData("no_evidence", "unknown", "no_activity_evidence", 0)]
    [InlineData("scorm_only", "unknown", "no_activity_evidence", 0)]
    public async Task PostExecution_requires_activity_evidence_and_separates_aggregate_risk(
        string scenario, string expected, string? reason, int below)
    {
        var items = ScenarioItems(scenario);
        var result = await new GeneratePostExecutionReportQueryHandler(
            new FakeParticipantsGateway(), new FakeGradebookGateway(items), new FakeCurrentUserGateway(),
            new SubmissionMediator(scenario, ["1"]))
            .Handle(new GeneratePostExecutionReportQuery("10"), CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.Equal(expected, row.OutcomeIndicator);
        Assert.Equal(reason, row.OutcomeReason);
        Assert.Equal(below, row.BelowMinimumCount);
        Assert.Equal(expected == "likely_complete" ? 1 : 0, result.LikelyComplete);
        Assert.Equal(expected == "pending_recovery" ? 1 : 0, result.PendingRecovery);
    }

    [Fact]
    public async Task Low_total_alone_is_visible_without_becoming_activity_or_recovery_recipient()
    {
        var gateway = new FakeGradebookGateway(ScenarioItems("total_only"));
        var participants = new FakeParticipantsGateway();
        var user = new FakeCurrentUserGateway();
        var below = await new GetStudentsBelowMinGradeQueryHandler(participants, gateway, user)
            .Handle(new GetStudentsBelowMinGradeQuery("10"), CancellationToken.None);
        Assert.Empty(below.Students);
        Assert.Empty(below.SuggestedRecipientIds);
        Assert.True(Assert.Single(below.CourseTotals).CourseTotal.BelowMinimum);

        var weekly = await new GenerateWeeklyPerformanceReportQueryHandler(participants, gateway, user)
            .Handle(new GenerateWeeklyPerformanceReportQuery("10"), CancellationToken.None);
        var weekRow = Assert.Single(weekly.Students);
        Assert.Equal(0, weekRow.BelowMinimumCount);
        Assert.Empty(weekRow.BelowMinimumItems);
        Assert.True(weekRow.CourseTotal?.BelowMinimum);
        Assert.Empty(weekly.SuggestedRecipientIdsForGrade);

        var council = await new GenerateClassCouncilReportQueryHandler(participants, gateway, user)
            .Handle(new GenerateClassCouncilReportQuery("10"), CancellationToken.None);
        var councilRow = Assert.Single(council.Students);
        Assert.Empty(councilRow.BelowMinimumItems);
        Assert.Equal(0, councilRow.BelowMinimumCount);
        Assert.True(councilRow.CourseTotal?.BelowMinimum);
        Assert.Equal(0, council.NeedRecovery);
    }

    private static IReadOnlyList<GradebookItem> ScenarioItems(string scenario)
    {
        var totalLow = scenario is "total_only" or "aggregate_low" or "both_low";
        var total = new GradebookItem("total", "Total sintético", "course", "", null,
            totalLow ? 40m : 80m, null, 0m, 100m, null, null, null, null, null, null);
        var activity = new GradebookItem("activity", "Atividade sintética", "assign", "activity", null,
            scenario == "awaiting" ? null : scenario is "activity_low" or "both_low" ? 40m : 80m,
            null, 0m, 100m, null, null, null,
            scenario == "awaiting" ? 100L : null, null, null);
        return scenario switch
        {
            "total_only" or "high_total_only" => [total],
            "no_evidence" => [],
            "scorm_only" => [activity with { ItemModule = "scorm", ItemType = "mod" }],
            "activity_low" => [activity],
            _ => [total, activity]
        };
    }

    [Fact]
    public async Task ClassCouncil_SeparatesCourseTotalAndIgnoresOptionalRecoveryAsPending()
    {
        var sut = new GenerateClassCouncilReportQueryHandler(
            new FakeParticipantsGateway(),
            new FakeGradebookGateway(),
            new FakeCurrentUserGateway());

        var result = await sut.Handle(
            new GenerateClassCouncilReportQuery("10", MinGradePercent: 60m),
            CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.Equal(1, row.BelowMinimumCount);
        Assert.True(row.CourseTotal?.BelowMinimum);
        Assert.Equal(0, row.PendingItemsCount);
        Assert.Equal("recovery_needed", row.SituationFlag);
    }

    [Fact]
    public async Task PostExecution_SeparatesCourseTotalAndIgnoresOptionalRecoveryAsPending()
    {
        var sut = new GeneratePostExecutionReportQueryHandler(
            new FakeParticipantsGateway(),
            new FakeGradebookGateway(),
            new FakeCurrentUserGateway());

        var result = await sut.Handle(
            new GeneratePostExecutionReportQuery("10", MinGradePercent: 60m),
            CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.Equal(1, row.BelowMinimumCount);
        Assert.True(row.CourseTotal?.BelowMinimum);
        Assert.Equal(0, row.PendingCount);
        Assert.Equal("pending_recovery", row.OutcomeIndicator);
    }

    [Fact]
    public async Task PostExecution_DoesNotRecommendRecoveryForOnlyUngradedActivities()
    {
        var sut = new GeneratePostExecutionReportQueryHandler(
            new FakeParticipantsGateway(),
            new UngradedOnlyGradebookGateway(),
            new FakeCurrentUserGateway());

        var result = await sut.Handle(new GeneratePostExecutionReportQuery("10"), CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.Equal(0, row.PendingCount);
        Assert.Null(row.UnknownAssignmentsCount);
        Assert.Equal("unknown", row.OutcomeIndicator);
        Assert.Equal(0, result.PendingRecovery);
    }

    [Fact]
    public async Task Unknown_access_evidence_does_not_escalate_a_low_activity_grade_to_never_accessed_risk()
    {
        var student = new CourseParticipantSummary(
            "unknown-access", "Estudante sintético", null, false, null, null, null, [], [],
            LastCourseAccessEvidence: ParticipantAccessEvidence.Unknown);
        var participants = new FakeParticipantsGateway(students: [student]);
        var gradebook = new FakeGradebookGateway(ScenarioItems("activity_low"));
        var currentUser = new FakeCurrentUserGateway();

        var postExecution = await new GeneratePostExecutionReportQueryHandler(
            participants, gradebook, currentUser)
            .Handle(new GeneratePostExecutionReportQuery("10"), CancellationToken.None);
        var council = await new GenerateClassCouncilReportQueryHandler(
            participants, gradebook, currentUser)
            .Handle(new GenerateClassCouncilReportQuery("10"), CancellationToken.None);

        Assert.Equal("pending_recovery", Assert.Single(postExecution.Students).OutcomeIndicator);
        Assert.Equal("recovery_needed", Assert.Single(council.Students).SituationFlag);
        Assert.False(council.Students.Single().NeverAccessed);
    }

    [Fact]
    public async Task StudentsBelowMinimum_SeparatesCourseTotalAndIncludesZeroGrade()
    {
        var sut = new GetStudentsBelowMinGradeQueryHandler(
            new FakeParticipantsGateway(),
            new FakeGradebookGateway(),
            new FakeCurrentUserGateway());

        var result = await sut.Handle(
            new GetStudentsBelowMinGradeQuery("10", MinGradePercent: 60m),
            CancellationToken.None);

        var student = Assert.Single(result.Students);
        Assert.Single(student.BelowMinimumItems);
        Assert.DoesNotContain(student.BelowMinimumItems, item => item.ItemType == "course");
        Assert.True(Assert.Single(result.CourseTotals).CourseTotal.BelowMinimum);
        Assert.DoesNotContain(student.BelowMinimumItems, item => item.ItemName.Contains("recupera", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<GradebookItem> GradebookItems =>
    [
        new GradebookItem("course", "Total do curso", "course", "", null,
            47m, "47", 0m, 100m, null, null, null, null, null, null),
        new GradebookItem("zero", "Momento presencial", "assign", "activity", null,
            0m, "0", 0m, 26m, null, null, null, null, null, null),
        new GradebookItem("recovery", "SAP Recuperação", "assign", "activity", null,
            null, null, 0m, 100m, null, null, null, null, null, null)
    ];

    private sealed class FakeParticipantsGateway(bool hasMore = false,
        IReadOnlyList<CourseParticipantSummary>? students = null) : IMoodleParticipantsGateway
    {
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");
        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
            string userExternalId, string courseId, ParticipantStatusFilter statusFilter,
            int page, int pageSize, bool studentsOnly, bool includeEmail, string? groupId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CourseParticipantsPage(
                courseId, page, pageSize, statusFilter, studentsOnly, includeEmail,
                HasMore: hasMore,
                Participants: students ??
                [
                    new CourseParticipantSummary(
                        "1", "Lavinia", null, false, null,
                        DateTimeOffset.UtcNow.AddDays(-1),
                        DateTimeOffset.UtcNow.AddDays(-1), [], [])
                ]));

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);
    }

    private sealed class FakeGradebookGateway(IReadOnlyList<GradebookItem>? items = null,
        IReadOnlyDictionary<string, string>? scenarios = null) : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId, string studentId, CancellationToken cancellationToken) =>
            Task.FromResult(new CourseGradebook(courseId, studentId,
                scenarios is not null ? ScenarioItems(scenarios[studentId]) : items ?? GradebookItems));
    }

    private sealed class UngradedOnlyGradebookGateway : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(string courseId, string studentId, CancellationToken cancellationToken) =>
            Task.FromResult(new CourseGradebook(courseId, studentId,
            [new GradebookItem("assignment", "Atividade entregue", "assign", "activity", null,
                null, null, 0m, 100m, null, null, null, null, null, null)]));
    }

    private sealed class FakeCurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult(42L);
    }

    private sealed class SubmissionMediator(string mode, IReadOnlyList<string>? studentIds = null) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (mode == "failure") throw new InvalidOperationException("Falha sintética de leitura.");
            var completeEvidence = mode is "complete" or "all_pass" or "awaiting";
            var deliveredStudents = mode == "complete" ? studentIds ?? [] : ["1"];
            var evaluations = completeEvidence
                ? deliveredStudents.Select(studentId => new SubmissionEvaluationItem(
                    "10", "activity", "Atividade sintética", studentId,
                    mode == "awaiting" ? SubmissionEvaluationState.AwaitingGrading : SubmissionEvaluationState.GradedNumeric,
                    DateTimeOffset.UtcNow.AddMinutes(-5))
                {
                    DeliveryState = SubmissionDeliveryState.Submitted,
                }).ToArray()
                : [];
            var result = new GetStudentsWithPendingSubmissionsResult("10", 1, 0, [], [], null)
            {
                IsComplete = mode != "partial",
                Evaluations = evaluations,
                AwaitingGrading = mode == "awaiting"
                    ? [new AwaitingGradingSubmission("10", "activity", "Atividade sintética", "1",
                        "Estudante sintético", null, "submitted", DateTimeOffset.UtcNow.AddMinutes(-5), "notgraded")]
                    : [],
                DeliveryCoverage = new SubmissionDeliveryCoverage(
                    ParticipantsComplete: true,
                    InventoryComplete: completeEvidence,
                    EligibleAssignmentCount: completeEvidence ? 1 : null,
                    ObservedAssignmentCount: completeEvidence ? 1 : 0,
                    SubmissionReadsComplete: completeEvidence,
                    IsComplete: completeEvidence,
                    ReasonCode: completeEvidence ? null : "activity_coverage_unavailable"),
            };
            return Task.FromResult((TResponse)(object)result);
        }
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
