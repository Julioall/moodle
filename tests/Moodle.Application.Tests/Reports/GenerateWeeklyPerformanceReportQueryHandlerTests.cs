using Moodle.Application.Abstractions;
using MediatR;
using Moodle.Application.Reports.Queries;
using Moodle.Application.Submissions.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Reports;

public sealed class GenerateWeeklyPerformanceReportQueryHandlerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static CourseParticipantSummary MakeStudent(
        string id, string name, DateTimeOffset? lastAccess = null) =>
        new(UserId: id, FullName: name, Email: null, Suspended: false,
            FirstAccessAt: null, LastAccessAt: null, LastCourseAccessAt: lastAccess,
            Roles: [], Groups: [],
            LastAccessEvidence: lastAccess.HasValue ? ParticipantAccessEvidence.Observed : ParticipantAccessEvidence.Never,
            LastCourseAccessEvidence: lastAccess.HasValue ? ParticipantAccessEvidence.Observed : ParticipantAccessEvidence.Never);

    private static GradebookItem MakeGradeItem(
        string id, string name, decimal? gradeRaw, decimal gradeMax = 100m)
    {
        // Compute percentage so that BelowMinimum logic works in GradebookMappingHelper
        decimal? pct = gradeRaw.HasValue && gradeMax > 0
            ? gradeRaw.Value / gradeMax * 100m
            : null;
        return new(id, name, ItemType: "assign", ItemModule: "activity",
            CategoryId: null, GradeRaw: gradeRaw, GradeFormatted: null,
            GradeMin: 0m, GradeMax: gradeMax, PercentageFormatted: pct,
            Feedback: null, FeedbackFormat: null,
            GradedDateSubmitted: null, GradedDateGraded: null, GraderId: null);
    }

    private static GenerateWeeklyPerformanceReportQueryHandler CreateHandler(
        IReadOnlyList<CourseParticipantSummary> students,
        Dictionary<string, IReadOnlyList<GradebookItem>>? gradesByStudent = null,
        IMediator? mediator = null)
    {
        return new GenerateWeeklyPerformanceReportQueryHandler(
            new FakeParticipantsGateway(students),
            new FakeGradebookGateway(gradesByStudent ?? []),
            new FakeCurrentUserGateway(),
            mediator);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_NoStudents_ReturnsEmptyWithWarning()
    {
        var handler = CreateHandler([]);
        var result = await handler.Handle(
            new GenerateWeeklyPerformanceReportQuery("course1"), CancellationToken.None);

        Assert.Equal(0, result.TotalStudents);
        Assert.Equal(0, result.StudentsAtRisk);
        Assert.Contains("Nenhum estudante ativo", result.Warning);
    }

    [Fact]
    public async Task Handle_StudentNeverAccessedAndBelowMinimum_IsRisk()
    {
        var students = new[] { MakeStudent("1", "João Silva", lastAccess: null) };
        var grades = new Dictionary<string, IReadOnlyList<GradebookItem>>
        {
            ["1"] = [MakeGradeItem("item1", "SA 1", gradeRaw: 40m)]  // below 60%
        };

        var handler = CreateHandler(students, grades);
        var result = await handler.Handle(
            new GenerateWeeklyPerformanceReportQuery("course1", MinGradePercent: 60m, InactiveDaysThreshold: 7),
            CancellationToken.None);

        Assert.Equal(1, result.TotalStudents);
        Assert.Equal(1, result.StudentsAtRisk);

        var student = result.Students.Single();
        Assert.True(student.NeverAccessed);
        Assert.Equal("risk", student.AttentionLevel);
        Assert.Equal(1, student.BelowMinimumCount);
    }

    [Fact]
    public async Task Handle_DoesNotTreatUnknownAccessAsNeverAccessed()
    {
        var student = new CourseParticipantSummary("unknown", "Estudante sintético", null, false,
            null, null, null, [], [], LastAccessEvidence: ParticipantAccessEvidence.Unknown,
            LastCourseAccessEvidence: ParticipantAccessEvidence.Unknown);
        var result = await CreateHandler([student]).Handle(
            new GenerateWeeklyPerformanceReportQuery("course1"), CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.False(row.NeverAccessed);
        Assert.False(row.AccessCoverageAvailable);
    }

    [Fact]
    public async Task Handle_ActiveStudentWithGoodGrades_IsOk()
    {
        var recentAccess = DateTimeOffset.UtcNow.AddDays(-2);
        var students = new[] { MakeStudent("2", "Maria Souza", recentAccess) };
        var grades = new Dictionary<string, IReadOnlyList<GradebookItem>>
        {
            ["2"] = [MakeGradeItem("item1", "SA 1", gradeRaw: 80m)]  // above 60%
        };

        var handler = CreateHandler(students, grades, EmptyInventorySubmissionMediator());
        var result = await handler.Handle(
            new GenerateWeeklyPerformanceReportQuery("course1", MinGradePercent: 60m, InactiveDaysThreshold: 7),
            CancellationToken.None);

        Assert.Equal(1, result.TotalStudents);
        Assert.Equal(0, result.StudentsAtRisk);
        Assert.Equal(0, result.StudentsWithAttention);
        Assert.Equal("ok", result.Students.Single().AttentionLevel);
    }

    [Fact]
    public async Task Handle_ActiveStudentWithOneBelowMin_IsAttention()
    {
        var recentAccess = DateTimeOffset.UtcNow.AddDays(-2);
        var students = new[] { MakeStudent("3", "Carlos Lima", recentAccess) };
        var grades = new Dictionary<string, IReadOnlyList<GradebookItem>>
        {
            ["3"] = [MakeGradeItem("item1", "SA 1", gradeRaw: 45m)]  // below 60%, 1 factor
        };

        var handler = CreateHandler(students, grades);
        var result = await handler.Handle(
            new GenerateWeeklyPerformanceReportQuery("course1", MinGradePercent: 60m, InactiveDaysThreshold: 7),
            CancellationToken.None);

        Assert.Equal(1, result.StudentsWithAttention);
        Assert.Equal(0, result.StudentsAtRisk);
        Assert.Equal("attention", result.Students.Single().AttentionLevel);
    }

    [Fact]
    public async Task Handle_SeparatesCourseTotalAndDoesNotTreatRecoveryAsPending()
    {
        var students = new[] { MakeStudent("4", "Lavinia", DateTimeOffset.UtcNow.AddDays(-1)) };
        var grades = new Dictionary<string, IReadOnlyList<GradebookItem>>
        {
            ["4"] =
            [
                new GradebookItem("course", "Total do curso", "course", "", null,
                    47m, "47", 0m, 100m, null, null, null, null, null, null),
                new GradebookItem("zero", "Momento presencial", "assign", "activity", null,
                    0m, "0", 0m, 26m, null, null, null, null, null, null),
                new GradebookItem("recovery", "SAP Recuperação", "assign", "activity", null,
                    null, null, 0m, 100m, null, null, null, null, null, null)
            ]
        };

        var handler = CreateHandler(students, grades,
            SubmissionEvidenceMediator("4", SubmissionEvaluationState.Unknown, complete: false));
        var result = await handler.Handle(
            new GenerateWeeklyPerformanceReportQuery("course1", MinGradePercent: 60m),
            CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.Equal(1, row.BelowMinimumCount);
        Assert.DoesNotContain(row.BelowMinimumItems, item => item.ItemType == "course");
        Assert.True(row.CourseTotal?.BelowMinimum);
        Assert.Equal(1, row.TotalAssignments); // recovery is optional, not a universal pending item
        Assert.Equal(1, row.SubmittedCount);
        Assert.Equal(0, row.PendingCount);
        Assert.Equal("attention", row.AttentionLevel); // uma nota abaixo do mínimo é evidência positiva
        Assert.False(row.EvaluationCoverageAvailable);
        Assert.Empty(result.SuggestedRecipientIdsForPending);
    }

    [Fact]
    public async Task Handle_ExcludesScormFromPendingAndSeparatesAwaitingGrading()
    {
        var students = new[] { MakeStudent("5", "Pedro", DateTimeOffset.UtcNow.AddDays(-1)) };
        var grades = new Dictionary<string, IReadOnlyList<GradebookItem>>
        {
            ["5"] =
            [
                new GradebookItem("scorm", "Conteúdo do Curso", "mod", "scorm", null,
                    null, null, 0m, null, null, null, null, null, null, null),
                new GradebookItem("assign", "SA aguardando correção", "mod", "assign", null,
                    null, null, 0m, 100m, null, null, null,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null)
            ]
        };

        var result = await CreateHandler(students, grades,
            SubmissionEvidenceMediator("5", SubmissionEvaluationState.AwaitingGrading, complete: true,
                awaiting: true, assignmentName: "SA aguardando correção")).Handle(
            new GenerateWeeklyPerformanceReportQuery("course1"), CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.Equal(1, row.TotalAssignments);
        Assert.Equal(0, row.PendingCount);
        Assert.Equal(1, row.AwaitingGradingCount);
        Assert.Equal("SA aguardando correção", Assert.Single(row.AwaitingGradingAssignmentNames));
        Assert.Empty(result.SuggestedRecipientIdsForPending);
    }

    [Fact]
    public async Task Handle_RiskStudentSortedBeforeOkStudent()
    {
        var now = DateTimeOffset.UtcNow;
        var students = new[]
        {
            MakeStudent("ok1",   "Maria OK",   now.AddDays(-1)),
            MakeStudent("risk1", "João Risco", lastAccess: null),
        };
        var grades = new Dictionary<string, IReadOnlyList<GradebookItem>>
        {
            ["ok1"]   = [MakeGradeItem("i1", "SA 1", 80m)],
            ["risk1"] = [MakeGradeItem("i2", "SA 1", 30m)],
        };

        var handler = CreateHandler(students, grades);
        var result = await handler.Handle(
            new GenerateWeeklyPerformanceReportQuery("course1"), CancellationToken.None);

        Assert.Equal("risk1", result.Students.First().StudentId);
        Assert.Equal("ok1",   result.Students.Last().StudentId);
    }

    [Fact]
    public async Task Handle_InactiveStudentInAccessRecipients_ActiveStudentNot()
    {
        var students = new[]
        {
            MakeStudent("inactive1", "Carlos Inativo", lastAccess: null),
            MakeStudent("active1",   "Ana Ativa",      DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var handler = CreateHandler(students);
        var result = await handler.Handle(
            new GenerateWeeklyPerformanceReportQuery("course1", InactiveDaysThreshold: 7),
            CancellationToken.None);

        Assert.Single(result.SuggestedRecipientIdsForAccess);
        Assert.Equal("inactive1", result.SuggestedRecipientIdsForAccess.Single());
        Assert.DoesNotContain("active1", result.SuggestedRecipientIdsForAccess);
    }

    [Fact]
    public async Task Handle_Nao_reintroduz_pendencias_do_gradebook_quando_leitura_de_entregas_falha()
    {
        var students = new[] { MakeStudent("6", "Ana", DateTimeOffset.UtcNow.AddDays(-1)) };
        var grades = new Dictionary<string, IReadOnlyList<GradebookItem>>
        {
            ["6"] = [MakeGradeItem("future", "Atividade futura", gradeRaw: null)]
        };
        var handler = new GenerateWeeklyPerformanceReportQueryHandler(
            new FakeParticipantsGateway(students),
            new FakeGradebookGateway(grades),
            new FakeCurrentUserGateway(),
            new ThrowingMediator());

        var result = await handler.Handle(
            new GenerateWeeklyPerformanceReportQuery("course1"), CancellationToken.None);

        var row = Assert.Single(result.Students);
        Assert.Null(row.TotalAssignments);
        Assert.Null(row.UnknownAssignmentsCount);
        Assert.Equal(0, row.PendingCount);
        Assert.Empty(row.PendingAssignmentNames);
        Assert.Contains("leitura de entregas não ficou disponível", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_delivery_coverage_keeps_confirmed_count_and_exposes_unknown_total()
    {
        var students = new[] { MakeStudent("partial", "Estudante sintético", DateTimeOffset.UtcNow.AddDays(-1)) };
        var report = await CreateHandler(students, mediator: PartialDeliveryMediator()).Handle(
            new GenerateWeeklyPerformanceReportQuery("course1"), CancellationToken.None);

        var row = Assert.Single(report.Students);
        Assert.Equal(2, row.TotalAssignments);
        Assert.Equal(1, row.SubmittedCount);
        Assert.Equal(0, row.PendingCount);
        Assert.Equal(1, row.UnknownAssignmentsCount);
        Assert.False(row.ActivityCoverageAvailable);
        Assert.Equal("unknown", row.AttentionLevel);
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────────

    private sealed class FakeParticipantsGateway(IReadOnlyList<CourseParticipantSummary> students)
        : IMoodleParticipantsGateway
    {
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");
        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
            string userExternalId, string courseId, ParticipantStatusFilter statusFilter,
            int page, int pageSize, bool studentsOnly, bool includeEmail, string? groupId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CourseParticipantsPage(courseId, page, pageSize,
                statusFilter, studentsOnly, includeEmail, HasMore: false, students));

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);
    }

    private sealed class FakeGradebookGateway(
        Dictionary<string, IReadOnlyList<GradebookItem>> gradesByStudent)
        : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(
            string courseId, string studentId, CancellationToken cancellationToken)
        {
            var items = gradesByStudent.TryGetValue(studentId, out var g) ? g : [];
            return Task.FromResult(new CourseGradebook(courseId, studentId, items));
        }
    }

    private sealed class FakeCurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult(42L);
    }

    private sealed class ThrowingMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("falha simulada na leitura de entregas");

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("falha simulada na leitura de entregas");

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<TResponse>();

        public IAsyncEnumerable<object?> CreateStream(
            object request,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();
    }

    private static IMediator EmptyInventorySubmissionMediator() => new SubmissionMediator(
        new GetStudentsWithPendingSubmissionsResult("course1", 1, 0, [], [], null)
        {
            DeliveryCoverage = new SubmissionDeliveryCoverage(true, true, 0, 0, true, true, null),
            IsComplete = true,
        });

    private static IMediator PartialDeliveryMediator() => new SubmissionMediator(
        new GetStudentsWithPendingSubmissionsResult("course1", 1, 0, [], [], "submission_read_incomplete")
        {
            Evaluations =
            [
                new SubmissionEvaluationItem("course1", "assignment-1", "Atividade sintética", "partial",
                    SubmissionEvaluationState.Unknown, DateTimeOffset.UtcNow.AddMinutes(-5))
                {
                    DeliveryState = SubmissionDeliveryState.Submitted,
                }
            ],
            IsComplete = false,
            IncompleteReasonCode = "submission_read_incomplete",
            DeliveryCoverage = new SubmissionDeliveryCoverage(true, true, 2, 1, false, false,
                "submission_read_incomplete"),
        });

    private static IMediator SubmissionEvidenceMediator(
        string studentId,
        SubmissionEvaluationState evaluationState,
        bool complete,
        bool awaiting = false,
        string assignmentName = "Atividade sintética") => new SubmissionMediator(
        new GetStudentsWithPendingSubmissionsResult("course1", 1, 0, [], [], null)
        {
            Evaluations =
            [
                new SubmissionEvaluationItem("course1", "assignment-1", assignmentName, studentId,
                    evaluationState, DateTimeOffset.UtcNow.AddMinutes(-5))
                {
                    DeliveryState = SubmissionDeliveryState.Submitted,
                }
            ],
            AwaitingGrading = awaiting
                ? [new AwaitingGradingSubmission("course1", "assignment-1", assignmentName, studentId,
                    "Estudante sintético", null, "submitted", DateTimeOffset.UtcNow.AddMinutes(-5), "notgraded")]
                : [],
            DeliveryCoverage = new SubmissionDeliveryCoverage(true, true, 1, 1, true, true, null),
            IsComplete = complete,
        });

    private sealed class SubmissionMediator(GetStudentsWithPendingSubmissionsResult result) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)result);
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(result);
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) => AsyncEnumerable.Empty<TResponse>();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<object?>();
    }
}
