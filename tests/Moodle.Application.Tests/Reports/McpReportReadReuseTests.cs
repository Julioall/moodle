using System.Text.Json;
using MediatR;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.Reports.Queries;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Reports;

public sealed class McpReportReadReuseTests
{
    [Fact]
    public async Task Weekly_report_reuses_complete_reads_without_changing_report_rows()
    {
        foreach (var studentCount in new[] { 20, 100, 500 })
        foreach (var assignmentCount in new[] { 0, 10, 100 })
        {
            var optimized = await RunWeeklyReportAsync(studentCount, assignmentCount, ignorePrefetched: false);
            var baseline = await RunWeeklyReportAsync(studentCount, assignmentCount, ignorePrefetched: true);
            var expectedAssignmentReads = assignmentCount == 0 ? 0 : 1;
            var expectedAssignmentFunctionCalls = (assignmentCount + 49) / 50;

            Assert.Equal(studentCount, optimized.Result.TotalStudents);
            Assert.Equal(studentCount, optimized.Result.Students.Count);
            Assert.Equal(1, optimized.ParticipantReads);
            Assert.Equal(1, optimized.GradebookReads);
            Assert.Equal(1, optimized.ContentReads);
            Assert.Equal(expectedAssignmentReads, optimized.SubmissionReads);
            Assert.Equal(expectedAssignmentReads, optimized.SettingsReads);
            Assert.Equal(expectedAssignmentReads, optimized.FeedbackReads);
            Assert.Equal(expectedAssignmentFunctionCalls, optimized.SubmissionFunctionCalls);
            Assert.Equal(expectedAssignmentFunctionCalls, optimized.FeedbackFunctionCalls);
            Assert.Equal(2, baseline.ParticipantReads);
            Assert.Equal(2, baseline.GradebookReads);
            Assert.Equal(1, baseline.ContentReads);
            Assert.Equal(expectedAssignmentReads, baseline.SubmissionReads);
            Assert.Equal(expectedAssignmentReads, baseline.SettingsReads);
            Assert.Equal(expectedAssignmentReads, baseline.FeedbackReads);
            Assert.Equal(expectedAssignmentFunctionCalls, baseline.SubmissionFunctionCalls);
            Assert.Equal(expectedAssignmentFunctionCalls, baseline.FeedbackFunctionCalls);
            Assert.Equal(
                JsonSerializer.Serialize(baseline.Result.Students),
                JsonSerializer.Serialize(optimized.Result.Students));
            Assert.Equal(baseline.Result.Warning, optimized.Result.Warning);
        }
    }

    [Fact]
    public async Task Weekly_report_reuses_complete_individual_fallback_gradebooks()
    {
        foreach (var studentCount in new[] { 20, 100, 500 })
        {
            var optimized = await RunWeeklyReportAsync(
                studentCount,
                assignmentCount: 0,
                ignorePrefetched: false,
                gradebookSourceMode: "individual_fallback");
            var baseline = await RunWeeklyReportAsync(
                studentCount,
                assignmentCount: 0,
                ignorePrefetched: true,
                gradebookSourceMode: "individual_fallback");

            Assert.Equal(studentCount, optimized.IndividualGradebookReads);
            Assert.Equal(studentCount * 2, baseline.IndividualGradebookReads);
            Assert.Equal(1, optimized.GradebookReads);
            Assert.Equal(2, baseline.GradebookReads);
            Assert.Equal(
                JsonSerializer.Serialize(baseline.Result.Students),
                JsonSerializer.Serialize(optimized.Result.Students));
            Assert.Equal(baseline.Result.Warning, optimized.Result.Warning);
        }
    }

    [Fact]
    public async Task Weekly_report_with_no_students_skips_gradebook_and_pending_reads()
    {
        var participants = new CountingParticipantsGateway(0);
        var gradebook = new CountingGradebookGateway();
        var contents = new CountingContentsGateway(0);
        var pendingHandler = new GetStudentsWithPendingSubmissionsQueryHandler(
            participants,
            new CountingSubmissionsGateway(),
            contents,
            new CurrentUserGateway(),
            new AssignmentSettingsGateway(0),
            gradebookGateway: gradebook);
        var mediator = new DispatchingMediator(pendingHandler, ignorePrefetched: false);
        var report = new GenerateWeeklyPerformanceReportQueryHandler(
            participants,
            gradebook,
            new CurrentUserGateway(),
            mediator);

        var result = await report.Handle(new GenerateWeeklyPerformanceReportQuery("course-1"), CancellationToken.None);

        Assert.Equal(0, result.TotalStudents);
        Assert.Equal(1, participants.Reads);
        Assert.Equal(0, gradebook.CourseReads);
        Assert.Equal(0, contents.Reads);
        Assert.Null(mediator.PendingQuery);
    }

    [Fact]
    public async Task Weekly_report_discards_partial_prefetched_gradebook_and_reads_live()
    {
        var participants = new CountingParticipantsGateway(20);
        var gradebook = new CountingGradebookGateway();
        var currentUser = new CurrentUserGateway();
        var pendingHandler = new GetStudentsWithPendingSubmissionsQueryHandler(
            participants,
            new CountingSubmissionsGateway(),
            new CountingContentsGateway(0),
            currentUser,
            new AssignmentSettingsGateway(0),
            new CountingAssignmentGradeReadGateway(),
            gradebook);
        var mediator = new DispatchingMediator(pendingHandler, ignorePrefetched: false);
        var partialGradebook = new CourseGradebookSnapshot(
            "course-1",
            new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase)
            {
                ["student-1"] = new CourseGradebook("course-1", "student-1", []),
            },
            new GradebookSnapshotCoverage(
                "bulk", 20, 1, IsComplete: false, Truncated: false,
                Enumerable.Range(2, 19).Select(index => $"student-{index}").ToArray(), []));
        var report = new GenerateWeeklyPerformanceReportQueryHandler(
            participants, gradebook, currentUser, mediator);

        var result = await report.Handle(
            new GenerateWeeklyPerformanceReportQuery(
                "course-1",
                MaxStudentsToAnalyze: 20,
                PrefetchedGradebook: partialGradebook,
                PrefetchedParticipants: Participants(20)),
            CancellationToken.None);

        Assert.Equal(20, result.TotalStudents);
        Assert.Equal(0, participants.Reads);
        Assert.Equal(1, gradebook.CourseReads);
        Assert.NotNull(mediator.PendingQuery!.PrefetchedGradebook);
        Assert.True(mediator.PendingQuery.PrefetchedGradebook.Coverage.IsComplete);
    }

    [Theory]
    [InlineData("class_council")]
    [InlineData("post_execution")]
    public async Task Other_derived_reports_reuse_the_same_complete_context(string reportType)
    {
        var optimized = await RunOtherReportAsync(reportType, ignorePrefetched: false);
        var baseline = await RunOtherReportAsync(reportType, ignorePrefetched: true);

        Assert.Equal(20, optimized.TotalStudents);
        Assert.Equal(1, optimized.ParticipantReads);
        Assert.Equal(1, optimized.GradebookReads);
        Assert.Equal(2, baseline.ParticipantReads);
        Assert.Equal(2, baseline.GradebookReads);
        Assert.Equal(baseline.RowsJson, optimized.RowsJson);
        Assert.Equal(baseline.Warning, optimized.Warning);
    }

    [Fact]
    public async Task Pending_weekly_council_and_post_execution_reports_reconcile_delivery_states()
    {
        var participants = new CountingParticipantsGateway(1);
        var gradebook = new CountingGradebookGateway();
        var currentUser = new CurrentUserGateway();
        var pendingHandler = new GetStudentsWithPendingSubmissionsQueryHandler(
            participants,
            new MixedStateSubmissionsGateway(),
            new CountingContentsGateway(2),
            currentUser,
            new AssignmentSettingsGateway(2),
            new CountingAssignmentGradeReadGateway(),
            gradebook);
        var mediator = new DispatchingMediator(pendingHandler, ignorePrefetched: false);
        var query = new GetStudentsWithPendingSubmissionsQuery(
            "course-1", MaxStudentsToAnalyze: 1, IncludeAwaitingGrading: true);

        var pending = await pendingHandler.Handle(query, CancellationToken.None);
        var weekly = await new GenerateWeeklyPerformanceReportQueryHandler(
                participants, gradebook, currentUser, mediator)
            .Handle(new GenerateWeeklyPerformanceReportQuery("course-1", MaxStudentsToAnalyze: 1), CancellationToken.None);
        var council = await new GenerateClassCouncilReportQueryHandler(
                participants, gradebook, currentUser, mediator)
            .Handle(new GenerateClassCouncilReportQuery("course-1", MaxStudentsToAnalyze: 1), CancellationToken.None);
        var postExecution = await new GeneratePostExecutionReportQueryHandler(
                participants, gradebook, currentUser, mediator)
            .Handle(new GeneratePostExecutionReportQuery("course-1", MaxStudentsToAnalyze: 1), CancellationToken.None);

        var weeklyRow = Assert.Single(weekly.Students);
        var councilRow = Assert.Single(council.Students);
        var postExecutionRow = Assert.Single(postExecution.Students);

        Assert.Equal(2, pending.DeliveryCoverage.EligibleAssignmentCount);
        Assert.Equal(0, pending.DeliveryCoverage.UnknownAssignmentsCount);
        Assert.Equal("assignment-1", Assert.Single(Assert.Single(pending.Students).PendingAssignments).AssignmentId);
        Assert.Equal("assignment-2", Assert.Single(pending.AwaitingGrading).AssignmentId);
        Assert.Equal(SubmissionDeliveryState.NotSubmitted, Assert.Single(pending.Evaluations,
            item => item.AssignmentId == "assignment-1").DeliveryState);
        Assert.Equal(SubmissionDeliveryState.Submitted, Assert.Single(pending.Evaluations,
            item => item.AssignmentId == "assignment-2").DeliveryState);
        Assert.Equal(MixedStateSubmissionsGateway.LatestAttemptAt,
            Assert.Single(pending.Evaluations, item => item.AssignmentId == "assignment-2").SubmittedAt);

        Assert.Equal(2, weeklyRow.TotalAssignments);
        Assert.Equal(1, weeklyRow.SubmittedCount);
        Assert.Equal(1, weeklyRow.PendingCount);
        Assert.Equal(1, weeklyRow.AwaitingGradingCount);
        Assert.Equal(2, councilRow.EligibleAssignmentCount);
        Assert.Equal(1, councilRow.PendingItemsCount);
        Assert.Equal(1, councilRow.AwaitingGradingItemsCount);
        Assert.Equal(2, postExecutionRow.EligibleAssignmentCount);
        Assert.Equal(1, postExecutionRow.PendingCount);
        Assert.Equal(1, postExecutionRow.AwaitingGradingCount);
        Assert.Equal(pending.DeliveryCoverage, weekly.DeliveryCoverage);
        Assert.Equal(pending.DeliveryCoverage, council.DeliveryCoverage);
        Assert.Equal(pending.DeliveryCoverage, postExecution.DeliveryCoverage);
    }

    [Fact]
    public async Task Pending_query_reloads_truncated_or_wrong_course_participants()
    {
        foreach (var prefetched in new[]
        {
            Participants(20, hasMore: true),
            Participants(20, courseId: "other-course"),
            Participants(20, studentsOnly: false),
            Participants(20, statusFilter: ParticipantStatusFilter.Suspended),
        })
        {
            var participants = new CountingParticipantsGateway(20);
            var contents = new CountingContentsGateway(0);
            var handler = new GetStudentsWithPendingSubmissionsQueryHandler(
                participants,
                new CountingSubmissionsGateway(),
                contents,
                new CurrentUserGateway(),
                new AssignmentSettingsGateway(0));

            await handler.Handle(
                new GetStudentsWithPendingSubmissionsQuery(
                    "course-1",
                    MaxStudentsToAnalyze: 20,
                    PrefetchedParticipants: prefetched),
                CancellationToken.None);

            Assert.Equal(1, participants.Reads);
            Assert.Equal(1, contents.Reads);
        }
    }

    [Fact]
    public async Task Pending_query_reloads_incomplete_or_wrong_course_gradebook()
    {
        foreach (var prefetched in new[]
        {
            new CourseGradebookSnapshot(
                "course-1",
                new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase),
                new GradebookSnapshotCoverage(
                    "bulk", 20, 0, IsComplete: false, Truncated: false, Enumerable.Range(1, 20).Select(i => $"student-{i}").ToArray(), [])),
            new CourseGradebookSnapshot(
                "other-course",
                new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase),
                new GradebookSnapshotCoverage(
                    "bulk", 20, 0, IsComplete: true, Truncated: false, [], [])),
        })
        {
            var participants = new CountingParticipantsGateway(20);
            var gradebook = new CountingGradebookGateway();
            var handler = new GetStudentsWithPendingSubmissionsQueryHandler(
                participants,
                new CountingSubmissionsGateway(),
                new CountingContentsGateway(0),
                new CurrentUserGateway(),
                new AssignmentSettingsGateway(0),
                gradebookGateway: gradebook);

            var result = await handler.Handle(
                new GetStudentsWithPendingSubmissionsQuery(
                    "course-1",
                    MaxStudentsToAnalyze: 20,
                    IncludeAwaitingGrading: true,
                    PrefetchedParticipants: Participants(20),
                    PrefetchedGradebook: prefetched),
                CancellationToken.None);

            Assert.True(result.IsComplete);
            Assert.Equal(1, gradebook.CourseReads);
            Assert.Equal(0, participants.Reads);
        }
    }

    [Fact]
    public async Task Pending_query_keeps_successful_assignments_when_one_read_fails()
    {
        var participants = new CountingParticipantsGateway(1);
        var submissions = new CountingSubmissionsGateway("assignment-2");
        var handler = new GetStudentsWithPendingSubmissionsQueryHandler(
            participants,
            submissions,
            new CountingContentsGateway(2),
            new CurrentUserGateway(),
            new AssignmentSettingsGateway(2));

        var result = await handler.Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", MaxStudentsToAnalyze: 1),
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Contains("demais atividades foram processadas normalmente", result.Warning);
        Assert.Contains(result.Evaluations, evaluation => evaluation.AssignmentId == "assignment-1");
        Assert.DoesNotContain(result.Evaluations, evaluation => evaluation.AssignmentId == "assignment-2");
        Assert.Equal(1, submissions.Reads);
    }

    private static async Task<ScenarioResult> RunWeeklyReportAsync(
        int studentCount,
        int assignmentCount,
        bool ignorePrefetched,
        string gradebookSourceMode = "bulk")
    {
        var participants = new CountingParticipantsGateway(studentCount);
        var gradebook = new CountingGradebookGateway(gradebookSourceMode);
        var currentUser = new CurrentUserGateway();
        var contents = new CountingContentsGateway(assignmentCount);
        var submissions = new CountingSubmissionsGateway();
        var settings = new AssignmentSettingsGateway(assignmentCount);
        var feedback = new CountingAssignmentGradeReadGateway();
        var pendingHandler = new GetStudentsWithPendingSubmissionsQueryHandler(
            participants,
            submissions,
            contents,
            currentUser,
            settings,
            feedback,
            gradebookGateway: gradebook);
        var mediator = new DispatchingMediator(pendingHandler, ignorePrefetched);
        var report = new GenerateWeeklyPerformanceReportQueryHandler(
            participants,
            gradebook,
            currentUser,
            mediator);

        var result = await report.Handle(
            new GenerateWeeklyPerformanceReportQuery("course-1", MaxStudentsToAnalyze: studentCount),
            CancellationToken.None);
        return new ScenarioResult(
            result,
            participants.Reads,
            gradebook.CourseReads,
            contents.Reads,
            submissions.Reads,
            settings.Reads,
            feedback.Reads,
            gradebook.StudentReads,
            submissions.MoodleFunctionCalls,
            feedback.MoodleFunctionCalls);
    }

    private static async Task<OtherReportScenarioResult> RunOtherReportAsync(
        string reportType,
        bool ignorePrefetched)
    {
        const int studentCount = 20;
        var participants = new CountingParticipantsGateway(studentCount);
        var gradebook = new CountingGradebookGateway();
        var currentUser = new CurrentUserGateway();
        var contents = new CountingContentsGateway(0);
        var pendingHandler = new GetStudentsWithPendingSubmissionsQueryHandler(
            participants,
            new CountingSubmissionsGateway(),
            contents,
            currentUser,
            new AssignmentSettingsGateway(0),
            new CountingAssignmentGradeReadGateway(),
            gradebook);
        var mediator = new DispatchingMediator(pendingHandler, ignorePrefetched);

        string rowsJson;
        string? warning;
        int totalStudents;
        if (reportType == "class_council")
        {
            var report = await new GenerateClassCouncilReportQueryHandler(
                    participants, gradebook, currentUser, mediator)
                .Handle(
                    new GenerateClassCouncilReportQuery("course-1", MaxStudentsToAnalyze: studentCount),
                    CancellationToken.None);
            rowsJson = JsonSerializer.Serialize(report.Students);
            warning = report.Warning;
            totalStudents = report.TotalStudents;
        }
        else
        {
            var report = await new GeneratePostExecutionReportQueryHandler(
                    participants, gradebook, currentUser, mediator)
                .Handle(
                    new GeneratePostExecutionReportQuery("course-1", MaxStudentsToAnalyze: studentCount),
                    CancellationToken.None);
            rowsJson = JsonSerializer.Serialize(report.Students);
            warning = report.Warning;
            totalStudents = report.TotalStudents;
        }

        return new OtherReportScenarioResult(
            rowsJson,
            warning,
            totalStudents,
            participants.Reads,
            gradebook.CourseReads);
    }

    private static CourseParticipantsPage Participants(
        int studentCount,
        string courseId = "course-1",
        bool hasMore = false,
        bool studentsOnly = true,
        ParticipantStatusFilter statusFilter = ParticipantStatusFilter.Active) =>
        new(
            courseId,
            1,
            studentCount,
            statusFilter,
            studentsOnly,
            IncludeEmail: false,
            hasMore,
            Enumerable.Range(1, studentCount)
                .Select(index => new CourseParticipantSummary(
                    $"student-{index}", $"Student {index}", null, false, null, null, null, [], []))
                .ToArray());

    private static CourseContentsSummary EmptyContents() =>
        new("course-1", ["assign"], IncludeHidden: false, OnlyWithFiles: false, []);

    private sealed record ScenarioResult(
        GenerateWeeklyPerformanceReportResult Result,
        int ParticipantReads,
        int GradebookReads,
        int ContentReads,
        int SubmissionReads,
        int SettingsReads,
        int FeedbackReads,
        int IndividualGradebookReads,
        int SubmissionFunctionCalls,
        int FeedbackFunctionCalls);

    private sealed record OtherReportScenarioResult(
        string RowsJson,
        string? Warning,
        int TotalStudents,
        int ParticipantReads,
        int GradebookReads);

    private sealed class CountingParticipantsGateway(int studentCount) : IMoodleParticipantsGateway
    {
        public int Reads { get; private set; }
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");

        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
            string userExternalId,
            string courseId,
            ParticipantStatusFilter statusFilter,
            int page,
            int pageSize,
            bool studentsOnly,
            bool includeEmail,
            string? groupId,
            CancellationToken cancellationToken)
        {
            Reads++;
            IReadOnlyList<CourseParticipantSummary> students = Enumerable.Range(1, studentCount)
                .Select(index => new CourseParticipantSummary(
                    $"student-{index}", $"Student {index}", null, false, null, null, null, [], []))
                .ToArray();
            return Task.FromResult(new CourseParticipantsPage(
                courseId, page, pageSize, statusFilter, studentsOnly, includeEmail,
                HasMore: false, students));
        }

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);
    }

    private sealed class CountingGradebookGateway(string sourceMode = "bulk") : IMoodleGradebookGateway
    {
        public int CourseReads { get; private set; }
        public int StudentReads { get; private set; }

        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId, string studentId, CancellationToken cancellationToken)
        {
            StudentReads++;
            return Task.FromResult(new CourseGradebook(courseId, studentId, []));
        }

        public async Task<CourseGradebookSnapshot> GetCourseGradebookAsync(
            string courseId,
            IReadOnlyCollection<string> studentIds,
            int? groupId,
            CancellationToken cancellationToken)
        {
            CourseReads++;
            var ids = studentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            IReadOnlyDictionary<string, CourseGradebook> gradebooks;
            if (string.Equals(sourceMode, "individual_fallback", StringComparison.Ordinal))
            {
                var reads = await Task.WhenAll(ids.Select(id => GetStudentGradebookAsync(courseId, id, cancellationToken)));
                gradebooks = ids.Zip(reads).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                gradebooks = ids.ToDictionary(
                    id => id,
                    id => new CourseGradebook(courseId, id, []),
                    StringComparer.OrdinalIgnoreCase);
            }
            return new CourseGradebookSnapshot(
                courseId,
                gradebooks,
                new GradebookSnapshotCoverage(
                    sourceMode, ids.Length, ids.Length, IsComplete: true, Truncated: false, [], [])
                {
                    ReturnedStudentIds = ids,
                });
        }
    }

    private sealed class CountingContentsGateway(int assignmentCount) : IMoodleCourseContentsGateway
    {
        public int Reads { get; private set; }

        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken)
        {
            Reads++;
            var modules = Enumerable.Range(1, assignmentCount)
                .Select(index => new CourseModuleSummary(
                    $"module-{index}",
                    $"assignment-{index}",
                    "assign",
                    $"Assignment {index}",
                    null,
                    true,
                    true,
                    null,
                    null,
                    [],
                    []))
                .ToArray();
            IReadOnlyList<CourseSectionSummary> sections =
            [
                new CourseSectionSummary("section-1", 1, "Section", null, true, modules.Length, modules.Length == 0, modules)
            ];
            return Task.FromResult(new CourseContentsSummary(
                courseId, moduleTypes.ToArray(), includeHidden, onlyWithFiles, sections));
        }
    }

    private sealed class CountingSubmissionsGateway(string? failedAssignmentId = null) : IMoodleAssignmentSubmissionsGateway
    {
        public int Reads { get; private set; }
        public int MoodleFunctionCalls { get; private set; }

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            Reads++;
            MoodleFunctionCalls += (assignmentIds.Count + 49) / 50;
            return Task.FromResult<IReadOnlyList<AssignmentSubmissionsBatch>>(
                assignmentIds.Select(id => string.Equals(id, failedAssignmentId, StringComparison.OrdinalIgnoreCase)
                    ? new AssignmentSubmissionsBatch(id, [], "read_failed", "Simulated read failure.")
                    : new AssignmentSubmissionsBatch(id, [])).ToArray());
        }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<AssignmentSubmissionRecord>>([]);
        }
    }

    private sealed class MixedStateSubmissionsGateway : IMoodleAssignmentSubmissionsGateway
    {
        internal static readonly DateTimeOffset LatestAttemptAt = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<AssignmentSubmissionsBatch> batches = assignmentIds.Select(id =>
                string.Equals(id, "assignment-2", StringComparison.OrdinalIgnoreCase)
                    ? new AssignmentSubmissionsBatch(id,
                    [
                        new AssignmentSubmissionRecord("submission-old", "student-1", "submitted", "notgraded",
                            LatestAttemptAt.AddMinutes(-10), LatestAttemptAt.AddMinutes(-10), 1, 0, false),
                        new AssignmentSubmissionRecord("submission-current", "student-1", "submitted", "notgraded",
                            LatestAttemptAt, LatestAttemptAt, 2, 0, false),
                    ])
                    : new AssignmentSubmissionsBatch(id, [])).ToArray();
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
            string userExternalId,
            string assignmentId,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssignmentSubmissionRecord>>([]);
    }

    private sealed class AssignmentSettingsGateway(int assignmentCount) : IMoodleAssignmentSettingsGateway
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            Reads++;
            IReadOnlyDictionary<string, AssignmentSettingsSummary> settings =
                Enumerable.Range(1, assignmentCount).ToDictionary(
                    index => $"assignment-{index}",
                    index => new AssignmentSettingsSummary($"assignment-{index}", 100m, $"Assignment {index}", IsGradable: true) { NoSubmissions = false },
                    StringComparer.Ordinal);
            return Task.FromResult(settings);
        }

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId, string courseId, string assignmentId, CancellationToken cancellationToken) =>
            Task.FromResult<AssignmentSettingsSummary?>(null);
    }

    private sealed class CountingAssignmentGradeReadGateway : IMoodleAssignmentGradeReadGateway
    {
        public int Reads { get; private set; }
        public int MoodleFunctionCalls { get; private set; }

        public Task<IReadOnlyList<AssignmentGradesBatch>> GetExistingGradesBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            IReadOnlyCollection<string> studentIds,
            CancellationToken cancellationToken)
        {
            Reads++;
            MoodleFunctionCalls += (assignmentIds.Count + 49) / 50;
            IReadOnlyList<AssignmentGradesBatch> batches = assignmentIds.Select(assignmentId =>
                new AssignmentGradesBatch(
                    assignmentId,
                    new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase))).ToArray();
            return Task.FromResult(batches);
        }

        public Task<AssignmentExistingGrade?> GetExistingGradeAsync(
            string userExternalId,
            string assignmentId,
            string studentId,
            CancellationToken cancellationToken) => Task.FromResult<AssignmentExistingGrade?>(null);
    }

    private sealed class CurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(42L);
    }

    private sealed class DispatchingMediator(
        GetStudentsWithPendingSubmissionsQueryHandler pendingHandler,
        bool ignorePrefetched) : IMediator
    {
        public GetStudentsWithPendingSubmissionsQuery? PendingQuery { get; private set; }

        public Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default)
        {
            if (request is not GetStudentsWithPendingSubmissionsQuery pending)
            {
                throw new NotSupportedException($"Unexpected query type: {request.GetType().Name}");
            }

            PendingQuery = pending;
            var dispatched = ignorePrefetched
                ? pending with { PrefetchedParticipants = null, PrefetchedGradebook = null }
                : pending;
            return (Task<TResponse>)(object)pendingHandler.Handle(dispatched, cancellationToken);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<object?>();
    }
}
