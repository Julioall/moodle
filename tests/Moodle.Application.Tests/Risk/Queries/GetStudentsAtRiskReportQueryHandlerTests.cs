using Moodle.Application.Abstractions;
using Moodle.Application.Risk.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Risk.Queries;

public sealed class GetStudentsAtRiskReportQueryHandlerTests
{
    [Fact]
    public async Task Handle_IdentificaRiscoPorInatividade()
    {
        var participants = new FakeParticipantsGateway();
        var gradebook = new FakeGradebookGateway();
        var currentUserId = new FakeCurrentUserIdGateway();

        var sut = new GetStudentsAtRiskReportQueryHandler(participants, gradebook, currentUserId);

        var result = await sut.Handle(new GetStudentsAtRiskReportQuery("10", 50, InactivityThresholdDays: 7, MinGradePercentage: 60m), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Reports);
        Assert.Equal("123", result.Reports[0].StudentId);
        Assert.Equal(RiskLevel.Medio, result.Reports[0].RiskLevel); // Factors: inactive and low grade
        Assert.Equal(1, result.ParticipantsAnalyzedCount);
        Assert.Equal(1, result.ClassificationDiagnostics.IncludedByFallbackCount);
        Assert.Equal(50m, result.Reports[0].CurrentGrade);
        Assert.Null(result.Reports[0].CompletionRate);
        Assert.Contains(result.Reports[0].Factors, factor => factor.Contains("Nota atual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Handle_Nao_classifica_acesso_sem_evidencia_como_nunca_acessado()
    {
        var participant = new CourseParticipantSummary(
            "unknown-access", "Estudante sintético", null, false, null, null, null, [], [],
            LastCourseAccessEvidence: ParticipantAccessEvidence.Unknown);
        var sut = new GetStudentsAtRiskReportQueryHandler(
            new FakeParticipantsGateway { ParticipantOverride = participant },
            new FakeGradebookGateway(),
            new FakeCurrentUserIdGateway());

        var result = await sut.Handle(
            new GetStudentsAtRiskReportQuery("10", 50, InactivityThresholdDays: 7, MinGradePercentage: 60m),
            CancellationToken.None);

        var report = Assert.Single(result.Reports);
        Assert.Equal(RiskLevel.Medio, report.RiskLevel);
        Assert.DoesNotContain(report.Factors, factor => factor.Contains("nunca acessou", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, result.AccessUnknownCount);
        Assert.False(result.AccessCoverageComplete);
        Assert.True(result.ParticipantCoverageComplete);
    }

    [Fact]
    public async Task Handle_DiagnosticaAusenciaDeParticipantes()
    {
        var sut = new GetStudentsAtRiskReportQueryHandler(
            new FakeParticipantsGateway { ReturnEmpty = true },
            new FakeGradebookGateway(),
            new FakeCurrentUserIdGateway());

        var result = await sut.Handle(
            new GetStudentsAtRiskReportQuery("10", 50), CancellationToken.None);

        Assert.Empty(result.Reports);
        Assert.Equal(0, result.ParticipantsAnalyzedCount);
    }

    [Fact]
    public async Task Handle_AgregaFalhasParciaisSemAbortarRelatorio()
    {
        var sut = new GetStudentsAtRiskReportQueryHandler(
            new FakeParticipantsGateway(),
            new ThrowingGradebookGateway(),
            new FakeCurrentUserIdGateway());

        var result = await sut.Handle(
            new GetStudentsAtRiskReportQuery("10", 50), CancellationToken.None);

        Assert.Equal(1, result.GradebookFailureCount);
        Assert.All(result.Reports, report => Assert.Null(report.CompletionRate));
    }

    private sealed class FakeParticipantsGateway : IMoodleParticipantsGateway
    {
        public bool ReturnEmpty { get; init; }
        public CourseParticipantSummary? ParticipantOverride { get; init; }
    public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This legacy fixture does not support limited previews.");

        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(string userExternalId, string courseId, ParticipantStatusFilter statusFilter, int page, int pageSize, bool studentsOnly, bool includeEmail, string? groupId, CancellationToken cancellationToken)
        {
            IReadOnlyList<CourseParticipantSummary> participants = ReturnEmpty
                ? Array.Empty<CourseParticipantSummary>()
                : ParticipantOverride is not null
                    ? [ParticipantOverride]
                    : [
                    new CourseParticipantSummary(
                        UserId: "123",
                        FullName: "Aluno Teste",
                        Email: "aluno@teste.com",
                        Suspended: false,
                        FirstAccessAt: DateTimeOffset.UtcNow.AddDays(-30),
                        LastAccessAt: DateTimeOffset.UtcNow.AddDays(-10),
                        LastCourseAccessAt: DateTimeOffset.UtcNow.AddDays(-10),
                        Roles: [],
                        Groups: [])
                ];

            return Task.FromResult(new CourseParticipantsPage(
                CourseId: courseId,
                Page: page,
                PageSize: pageSize,
                StatusFilter: statusFilter,
                StudentsOnly: studentsOnly,
                IncludeEmail: includeEmail,
                HasMore: false,
                Participants: participants,
                ClassificationDiagnostics: ReturnEmpty
                    ? ParticipantClassificationDiagnostics.Empty
                    : new ParticipantClassificationDiagnostics(
                        1, 0, 1, 0, true, true, ParticipantClassificationMode.Fallback)));
        }

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(string userExternalId, string courseId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);
        }
    }

    private sealed class ThrowingGradebookGateway : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(string courseId, string studentId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Falha simulada.");
    }

    private sealed class FakeGradebookGateway : IMoodleGradebookGateway
    {
        public Task<CourseGradebook> GetStudentGradebookAsync(string courseId, string studentId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new CourseGradebook(
                CourseId: courseId,
                StudentId: studentId,
                Items: [
                    new GradebookItem("1", "Course Total", "course", "", "", 50, "50,00", 0, 100, null, "", "", 0, 0, "")
                ]));
        }
    }

    private sealed class FakeCurrentUserIdGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(12345L);
        }
    }
}
