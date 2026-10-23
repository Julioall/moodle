using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Submissions;

public sealed class GetStudentsWithPendingSubmissionsQueryHandlerTests
{
    [Fact]
    public async Task Reports_real_absence_of_assign_without_marking_result_incomplete()
    {
        var fixture = new Fixture
        {
            Contents = Contents(),
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", IncludeAwaitingGrading: true),
            CancellationToken.None);

        Assert.Contains("Nenhuma atividade do tipo 'assign' foi encontrada", result.Warning);
        Assert.DoesNotContain("ou processada", result.Warning);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task Distinguishes_assign_processing_failure_from_real_absence()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            ThrowOnSubmissionRead = true,
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", IncludeAwaitingGrading: true),
            CancellationToken.None);

        Assert.Contains("Foram encontradas atividades do tipo 'assign', mas nenhuma foi processada", result.Warning);
        Assert.Contains("contagem deste curso está incompleta", result.Warning);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task Marks_truncated_assignment_scope_incomplete()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign"), Module("assign-2", "assign")),
            Submissions = [new AssignmentSubmissionsBatch("assign-1", [])],
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", MaxAssignmentsToAnalyze: 1),
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal("report_activity_limit", result.IncompleteReasonCode);
        Assert.Contains("limitada às 1 primeiras atividades", result.Warning);
        Assert.Equal(2, result.DeliveryCoverage.EligibleAssignmentCount);
        Assert.Equal(1, result.DeliveryCoverage.UnknownAssignmentsCount);
    }

    [Fact]
    public async Task Does_not_count_assignment_without_submission_mode_as_pending()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            AssignmentSettings = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assign-1"] = new("assign-1", 10) { NoSubmissions = true },
            },
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1"),
            CancellationToken.None);

        Assert.Empty(result.Evaluations);
        Assert.True(result.IsComplete);
        Assert.Contains("não aceitam submissões", result.Warning);
        Assert.Equal(1, fixture.FreshAssignmentSettingsReads);
        Assert.Equal(0, fixture.SubmissionReads);
    }

    [Fact]
    public async Task Fresh_unknown_submission_mode_wins_over_cached_known_mode()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            AssignmentSettings = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assign-1"] = new("assign-1", 100m) { NoSubmissions = false },
            },
            FreshAssignmentSettingsOverride = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal),
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1"),
            CancellationToken.None);

        Assert.Empty(result.Evaluations);
        Assert.False(result.IsComplete);
        Assert.Equal("activity_submission_mode_unknown", result.IncompleteReasonCode);
        Assert.Null(result.DeliveryCoverage.UnknownAssignmentsCount);
        Assert.Contains("modo de submissão", result.Warning);
        Assert.Equal(2, fixture.FreshAssignmentSettingsReads);
        Assert.Equal(0, fixture.AssignmentSettingsReads);
        Assert.Equal(0, fixture.SubmissionReads);
    }

    [Fact]
    public async Task Rechecks_live_assignment_settings_when_first_response_omits_submission_mode()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            Submissions = [new AssignmentSubmissionsBatch("assign-1", [])],
            FreshAssignmentSettingsResponses =
            [
                new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal),
                new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
                {
                    ["assign-1"] = new("assign-1", 100m) { NoSubmissions = false },
                },
            ],
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1"),
            CancellationToken.None);

        Assert.True(result.DeliveryCoverage.IsComplete);
        Assert.Equal(1, result.DeliveryCoverage.EligibleAssignmentCount);
        Assert.Equal(2, fixture.FreshAssignmentSettingsReads);
        Assert.Equal("assign-1", Assert.Single(result.Evaluations).AssignmentId);
    }

    [Fact]
    public async Task Retries_transient_failure_during_live_assignment_settings_read()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            Submissions = [new AssignmentSubmissionsBatch("assign-1", [])],
            AssignmentSettings = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assign-1"] = new("assign-1", 100m) { NoSubmissions = false },
            },
            FreshAssignmentSettingsTransientFailures = 1,
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1"),
            CancellationToken.None);

        Assert.True(result.DeliveryCoverage.IsComplete);
        Assert.Equal(1, result.DeliveryCoverage.EligibleAssignmentCount);
        Assert.Equal(2, fixture.FreshAssignmentSettingsReads);
    }

    [Fact]
    public async Task Marks_no_grade_submission_awaiting_when_grade_evidence_is_empty()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            Submissions = [new AssignmentSubmissionsBatch(
                "assign-1",
                [new AssignmentSubmissionRecord(
                    "submission-1",
                    "student-1",
                    "submitted",
                    "notgraded",
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow,
                    1,
                    0,
                    false)])],
            AssignmentSettings = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assign-1"] = new("assign-1", 0, "Atividade extra", IsGradable: false),
            },
            UseGradeRead = true,
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", IncludeAwaitingGrading: true),
            CancellationToken.None);

        Assert.Null(result.Warning);
        Assert.Single(result.AwaitingGrading);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task Keeps_evaluation_unknown_and_course_incomplete_when_feedback_read_fails()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            Submissions = [new AssignmentSubmissionsBatch(
                "assign-1",
                [new AssignmentSubmissionRecord("submission-1", "student-1", "submitted", "notgraded",
                    DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 1, 0, false)])],
            AssignmentSettings = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assign-1"] = new("assign-1", 0, "Atividade sintética", IsGradable: false),
            },
            UseGradeRead = true,
            ThrowOnFeedbackRead = true,
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", IncludeAwaitingGrading: true),
            CancellationToken.None);

        Assert.Empty(result.AwaitingGrading);
        Assert.Contains(result.Evaluations, item => item.State == SubmissionEvaluationState.Unknown);
        Assert.Contains(result.Evaluations, item => item.DeliveryState == SubmissionDeliveryState.Submitted);
        Assert.True(result.DeliveryCoverage.IsComplete);
        Assert.False(result.IsComplete);
        Assert.Contains("avaliações sem evidência conclusiva permanecem desconhecidas", result.Warning);
    }

    [Fact]
    public async Task One_read_separates_not_submitted_submitted_unknown_and_awaiting_grading()
    {
        var fixture = new Fixture
        {
            Contents = Contents(
                Module("not-submitted", "assign"),
                Module("submitted-unknown", "assign"),
                Module("awaiting", "assign"),
                Module("no-submission-required", "assign")),
            AssignmentSettings = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["not-submitted"] = new("not-submitted", 100m) { NoSubmissions = false },
                ["submitted-unknown"] = new("submitted-unknown", 100m) { NoSubmissions = false },
                ["awaiting"] = new("awaiting", 100m) { NoSubmissions = false },
                ["no-submission-required"] = new("no-submission-required", 0m) { NoSubmissions = true },
            },
            Submissions =
            [
                new AssignmentSubmissionsBatch("not-submitted", []),
                new AssignmentSubmissionsBatch("submitted-unknown",
                [new AssignmentSubmissionRecord("submission-1", "student-1", "submitted", "notgraded",
                    DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow, 1, 0, false)]),
                new AssignmentSubmissionsBatch("awaiting",
                [new AssignmentSubmissionRecord("submission-2", "student-1", "submitted", "notgraded",
                    DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 1, 0, false)]),
            ],
            UseGradeRead = true,
            ThrowOnFeedbackAssignmentId = "submitted-unknown",
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", IncludeAwaitingGrading: true),
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.True(result.DeliveryCoverage.IsComplete);
        Assert.Equal(3, result.DeliveryCoverage.EligibleAssignmentCount);
        Assert.Equal(3, result.DeliveryCoverage.ObservedAssignmentCount);
        Assert.Equal(0, result.DeliveryCoverage.UnknownAssignmentsCount);
        Assert.Equal(3, result.Evaluations.Count);
        Assert.Contains(result.Evaluations, item => item.AssignmentId == "not-submitted" &&
            item.DeliveryState == SubmissionDeliveryState.NotSubmitted);
        Assert.Contains(result.Evaluations, item => item.AssignmentId == "submitted-unknown" &&
            item.DeliveryState == SubmissionDeliveryState.Submitted &&
            item.State == SubmissionEvaluationState.Unknown);
        Assert.Contains(result.Evaluations, item => item.AssignmentId == "awaiting" &&
            item.DeliveryState == SubmissionDeliveryState.Submitted &&
            item.State == SubmissionEvaluationState.AwaitingGrading);
        Assert.Single(result.Students.Single().PendingAssignments);
        Assert.Equal("awaiting", Assert.Single(result.AwaitingGrading).AssignmentId);

        Assert.Equal("\"not_submitted\"", JsonSerializer.Serialize(SubmissionDeliveryState.NotSubmitted));
        Assert.Equal("\"submitted\"", JsonSerializer.Serialize(SubmissionDeliveryState.Submitted));
        Assert.Equal("\"unknown\"", JsonSerializer.Serialize(SubmissionDeliveryState.Unknown));
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"deliveryState\":\"not_submitted\"", json, StringComparison.Ordinal);
        Assert.Contains("\"deliveryState\":\"submitted\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Feedback_requires_explicit_current_review_to_leave_pending_queue(bool reviewed)
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            Submissions = [new AssignmentSubmissionsBatch(
                "assign-1",
                [
                    new AssignmentSubmissionRecord("submission-1", "student-1", "submitted", "notgraded", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 1, 0, false),
                    new AssignmentSubmissionRecord("submission-2", "student-2", "submitted", "notgraded", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 1, 0, false)
                ])],
            AssignmentSettings = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assign-1"] = new("assign-1", 0, "Atividade extra", IsGradable: false),
            },
            IncludeStudent2 = true,
            Grades = new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase)
            {
                ["student-1"] = new("assign-1", "student-1", null, false, Feedback: "Feedback sintético.",
                    GraderId: reviewed ? 42 : null,
                    TimeModified: reviewed ? DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds() : null),
                ["student-2"] = new("assign-1", "student-2", null, false, Feedback: null),
            }
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", IncludeAwaitingGrading: true),
            CancellationToken.None);

        Assert.Contains(result.AwaitingGrading, item => item.StudentId == "student-2");
        Assert.Equal(reviewed ? 1 : 2, result.AwaitingGrading.Count);
        Assert.Equal(!reviewed, result.AwaitingGrading.Any(item => item.StudentId == "student-1"));
    }

    [Fact]
    public async Task Returns_serializable_awaiting_grading_records_with_context()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            UseGradeRead = true,
            Submissions = [new AssignmentSubmissionsBatch(
                "assign-1",
                [new AssignmentSubmissionRecord("submission-1", "student-1", "submitted", "notgraded", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 1, 0, false)])]
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", IncludeAwaitingGrading: true),
            CancellationToken.None);

        var item = Assert.Single(result.AwaitingGrading);
        Assert.Equal("course-1", item.CourseId);
        Assert.Equal("assign-1", item.AssignmentId);
        Assert.Equal("student-1", item.StudentId);
        Assert.Equal("submitted", item.SubmissionStatus);
        Assert.Equal("awaiting_grading", item.GradingStatus);
        Assert.Contains("assignmentId", JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Infers_missing_students_as_pending_when_assignment_batch_is_successfully_empty()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            IncludeStudent2 = true,
            // Moodle can return a successful assignment entry with no
            // submission records for students who have not submitted.
            Submissions = [new AssignmentSubmissionsBatch("assign-1", [])]
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1"),
            CancellationToken.None);

        Assert.Equal(2, result.Students.Count);
        Assert.All(result.Students, student => Assert.Equal("assign-1", Assert.Single(student.PendingAssignments).AssignmentId));
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task Treats_new_submission_status_as_not_submitted()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            Submissions = [new AssignmentSubmissionsBatch(
                "assign-1",
                [new AssignmentSubmissionRecord(
                    "submission-1",
                    "student-1",
                    "new",
                    "notgraded",
                    null,
                    null,
                    0,
                    0,
                    false)])]
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1"),
            CancellationToken.None);

        var student = Assert.Single(result.Students);
        Assert.Equal("student-1", student.StudentId);
        Assert.Equal("assign-1", Assert.Single(student.PendingAssignments).AssignmentId);
    }

    [Fact]
    public async Task Reads_feedback_in_parallel_by_assignment_with_bounded_gateway_calls()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign"), Module("assign-2", "assign")),
            Submissions =
            [
                new AssignmentSubmissionsBatch("assign-1", [new AssignmentSubmissionRecord("s1", "student-1", "submitted", "notgraded", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 0, false)]),
                new AssignmentSubmissionsBatch("assign-2", [new AssignmentSubmissionRecord("s2", "student-1", "submitted", "notgraded", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 0, false)])
            ],
            AssignmentSettings = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal)
            {
                ["assign-1"] = new("assign-1", 0, "Extra 1", IsGradable: false),
                ["assign-2"] = new("assign-2", 0, "Extra 2", IsGradable: false),
            },
            Grades = new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase)
            {
                ["student-1"] = new("assign-1", "student-1", null, false, Feedback: null),
            }
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1", IncludeAwaitingGrading: true),
            CancellationToken.None);

        Assert.Equal(2, result.AwaitingGrading.Count);
        Assert.Equal(2, fixture.FeedbackReads);
    }

    [Fact]
    public async Task Reads_live_contents_before_using_prefetched_participants_for_empty_assign_scope()
    {
        var fixture = new Fixture
        {
            Contents = Contents(),
        };
        var participants = new CourseParticipantsPage(
            "course-1",
            1,
            100,
            ParticipantStatusFilter.Active,
            StudentsOnly: true,
            IncludeEmail: false,
            HasMore: false,
            Participants: [new CourseParticipantSummary("student-1", "Aluno 1", null, false, null, null, null, [], [])]);

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery(
                "course-1",
                IncludeAwaitingGrading: true,
                PrefetchedParticipants: participants),
            CancellationToken.None);

        Assert.Contains("Nenhuma atividade do tipo 'assign'", result.Warning);
        Assert.Equal(0, fixture.ParticipantReads);
        Assert.Equal(1, fixture.ContentReads);
        Assert.Equal(0, fixture.AssignmentSettingsReads);
        Assert.Equal(0, fixture.SubmissionReads);
    }

    [Fact]
    public async Task ExcludeFutureActivities_ignora_assign_ainda_nao_aberto()
    {
        var fixture = new Fixture
        {
            Contents = Contents(
                ModuleWithDates("assign-open", "assign", new CourseModuleDate("Abre:", DateTimeOffset.UtcNow.AddHours(-1))),
                ModuleWithDates("assign-future", "assign", new CourseModuleDate("Abre:", DateTimeOffset.UtcNow.AddDays(3)))),
            Submissions =
            [
                new AssignmentSubmissionsBatch("assign-open", []),
                new AssignmentSubmissionsBatch("assign-future", [])
            ]
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery(
                "course-1",
                IncludeAwaitingGrading: true,
                ExcludeFutureActivities: true),
            CancellationToken.None);

        var student = Assert.Single(result.Students);
        var pending = Assert.Single(student.PendingAssignments);
        Assert.Equal("assign-open", pending.AssignmentId);
        Assert.DoesNotContain(result.Evaluations, item => item.AssignmentId == "assign-future");
    }

    [Fact]
    public async Task Exclui_atividade_futura_por_padrao_para_manter_consistencia_das_pendencias()
    {
        var fixture = new Fixture
        {
            Contents = Contents(
                ModuleWithDates("assign-open", "assign", new CourseModuleDate("Abre:", DateTimeOffset.UtcNow.AddHours(-1))),
                ModuleWithDates("assign-future", "assign", new CourseModuleDate("Abre:", DateTimeOffset.UtcNow.AddDays(3)))),
            Submissions =
            [
                new AssignmentSubmissionsBatch("assign-open", []),
                new AssignmentSubmissionsBatch("assign-future", [])
            ]
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1"),
            CancellationToken.None);

        var student = Assert.Single(result.Students);
        Assert.Equal("assign-open", Assert.Single(student.PendingAssignments).AssignmentId);
        Assert.DoesNotContain(result.Evaluations, item => item.AssignmentId == "assign-future");
    }

    [Fact]
    public async Task Missing_assignment_batch_does_not_infer_not_submitted()
    {
        var fixture = new Fixture
        {
            Contents = Contents(Module("assign-1", "assign")),
            Submissions = [],
        };

        var result = await fixture.CreateHandler().Handle(
            new GetStudentsWithPendingSubmissionsQuery("course-1"),
            CancellationToken.None);

        Assert.Empty(result.Students);
        Assert.Empty(result.Evaluations);
        Assert.False(result.DeliveryCoverage.IsComplete);
        Assert.Equal("submission_read_incomplete", result.DeliveryCoverage.ReasonCode);
        Assert.Equal(1, result.DeliveryCoverage.EligibleAssignmentCount);
        Assert.Equal(0, result.DeliveryCoverage.ObservedAssignmentCount);
        Assert.Equal(1, result.DeliveryCoverage.UnknownAssignmentsCount);
    }

    private static CourseContentsSummary Contents(params CourseModuleSummary[] modules) =>
        new("course-1", ["assign"], false, false,
        [new CourseSectionSummary("section-1", 1, "Seção 1", null, true, modules.Length, modules.Length == 0, modules)]);

    private static CourseModuleSummary Module(string instanceId, string type) =>
        new($"module-{instanceId}", instanceId, type, "Atividade", null, true, true, null, null, [], []);

    private static CourseModuleSummary ModuleWithDates(
        string instanceId,
        string type,
        params CourseModuleDate[] dates) =>
        new($"module-{instanceId}", instanceId, type, "Atividade", null, true, true, null, null, dates, []);

    private sealed class Fixture
    {
        public CourseContentsSummary Contents { get; init; } = Contents();
        public IReadOnlyList<AssignmentSubmissionsBatch> Submissions { get; init; } = [];
        public IReadOnlyDictionary<string, AssignmentSettingsSummary> AssignmentSettings { get; init; } =
            new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal);
        public bool ThrowOnSubmissionRead { get; init; }
        public bool ThrowOnFeedbackRead { get; init; }
        public string? ThrowOnFeedbackAssignmentId { get; init; }
        public bool IncludeStudent2 { get; init; }
        public IReadOnlyDictionary<string, AssignmentSettingsSummary>? FreshAssignmentSettingsOverride { get; init; }
        public IReadOnlyList<IReadOnlyDictionary<string, AssignmentSettingsSummary>>? FreshAssignmentSettingsResponses { get; init; }
        public int FreshAssignmentSettingsTransientFailures { get; set; }
        public IReadOnlyDictionary<string, AssignmentExistingGrade> Grades { get; init; } =
            new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase);
        public bool UseGradeRead { get; init; }
        public int FeedbackReads { get; set; }
        public int ParticipantReads { get; set; }
        public int ContentReads { get; set; }
        public int AssignmentSettingsReads { get; set; }
        public int FreshAssignmentSettingsReads { get; set; }
        public int SubmissionReads { get; set; }

        public GetStudentsWithPendingSubmissionsQueryHandler CreateHandler() =>
            new(
                new ParticipantsGateway(this),
                new SubmissionsGateway(this),
                new ContentsGateway(this),
                new CurrentUserGateway(),
                new AssignmentSettingsGateway(this),
                Grades.Count > 0 || UseGradeRead ? new AssignmentGradeReadGateway(this) : null);
    }

    private sealed class ParticipantsGateway(Fixture fixture) : IMoodleParticipantsGateway
    {
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
            fixture.ParticipantReads++;
            var participants = fixture.IncludeStudent2
                ? new[]
                {
                    new CourseParticipantSummary("student-1", "Aluno 1", null, false, null, null, null, [], []),
                    new CourseParticipantSummary("student-2", "Aluno 2", null, false, null, null, null, [], [])
                }
                : new[] { new CourseParticipantSummary("student-1", "Aluno 1", null, false, null, null, null, [], []) };
            return Task.FromResult(new CourseParticipantsPage(courseId, page, pageSize, statusFilter, studentsOnly, includeEmail, false, participants));
        }

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);
    }

    private sealed class ContentsGateway(Fixture fixture) : IMoodleCourseContentsGateway
    {
        public Task<CourseContentsSummary> GetCourseContentsAsync(
            string userExternalId,
            string courseId,
            IReadOnlyCollection<string> moduleTypes,
            bool includeHidden,
            bool onlyWithFiles,
            CancellationToken cancellationToken)
        {
            fixture.ContentReads++;
            return Task.FromResult(fixture.Contents);
        }
    }

    private sealed class SubmissionsGateway(Fixture fixture) : IMoodleAssignmentSubmissionsGateway
    {
        public Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
            string userExternalId,
            IReadOnlyCollection<string> assignmentIds,
            string? status,
            DateTimeOffset? since,
            DateTimeOffset? before,
            CancellationToken cancellationToken)
        {
            fixture.SubmissionReads++;
            if (fixture.ThrowOnSubmissionRead) throw new InvalidOperationException("submission read failed");
            return Task.FromResult(fixture.Submissions);
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

    private sealed class CurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(1L);
    }

    private sealed class AssignmentSettingsGateway(Fixture fixture) : IMoodleAssignmentSettingsGateway
    {
        private IReadOnlyDictionary<string, AssignmentSettingsSummary> Settings
        {
            get
            {
                var settings = new Dictionary<string, AssignmentSettingsSummary>(
                    fixture.AssignmentSettings,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var module in fixture.Contents.Sections.SelectMany(section => section.Modules)
                             .Where(module => string.Equals(module.ModuleType, "assign", StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrWhiteSpace(module.InstanceId)))
                {
                    if (settings.TryGetValue(module.InstanceId!, out var configured))
                    {
                        if (configured.NoSubmissions is null)
                        {
                            settings[module.InstanceId!] = configured with { NoSubmissions = false };
                        }
                    }
                    else
                    {
                        settings.Add(module.InstanceId!, new AssignmentSettingsSummary(module.InstanceId!, 100m)
                        {
                            NoSubmissions = false,
                        });
                    }
                }

                return settings;
            }
        }

        public Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            fixture.AssignmentSettingsReads++;
            return Task.FromResult(Settings);
        }

        public Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsFreshAsync(
            string userExternalId,
            string courseId,
            CancellationToken cancellationToken)
        {
            fixture.FreshAssignmentSettingsReads++;
            if (fixture.FreshAssignmentSettingsTransientFailures > 0)
            {
                fixture.FreshAssignmentSettingsTransientFailures--;
                throw new HttpRequestException("transient settings read failure");
            }
            if (fixture.FreshAssignmentSettingsResponses is { Count: > 0 } responses)
            {
                var index = Math.Min(fixture.FreshAssignmentSettingsReads - 1, responses.Count - 1);
                return Task.FromResult(responses[index]);
            }
            return Task.FromResult(fixture.FreshAssignmentSettingsOverride ?? Settings);
        }

        public Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
            string userExternalId,
            string courseId,
            string assignmentId,
            CancellationToken cancellationToken) => Task.FromResult<AssignmentSettingsSummary?>(null);
    }

    private sealed class AssignmentGradeReadGateway(Fixture fixture) : IMoodleAssignmentGradeReadGateway
    {
        public Task<AssignmentExistingGrade?> GetExistingGradeAsync(string userExternalId, string assignmentId, string studentId, CancellationToken cancellationToken) =>
            Task.FromResult<AssignmentExistingGrade?>(fixture.Grades.GetValueOrDefault(studentId));

        public async Task<IReadOnlyDictionary<string, AssignmentExistingGrade>> GetExistingGradesAsync(
            string userExternalId,
            string assignmentId,
            IReadOnlyCollection<string> studentIds,
            CancellationToken cancellationToken)
        {
            fixture.FeedbackReads++;
            await Task.Delay(1, cancellationToken);
            if (fixture.ThrowOnFeedbackRead ||
                string.Equals(fixture.ThrowOnFeedbackAssignmentId, assignmentId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("feedback read failed");
            }
            return fixture.Grades;
        }
    }

}
