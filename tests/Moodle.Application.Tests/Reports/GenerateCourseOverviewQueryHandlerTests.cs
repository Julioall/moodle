using Moodle.Application.Abstractions;
using Moodle.Application.Reports.Queries;
using Moodle.Domain;

namespace Moodle.Application.Tests.Reports;

public sealed class GenerateCourseOverviewQueryHandlerTests
{
    [Fact]
    public async Task Unknown_access_is_not_counted_as_never_accessed_or_satisfactory_coverage()
    {
        var students = new[]
        {
            new CourseParticipantSummary("unknown", "Estudante sintético", null, false,
                null, null, null, [], [], LastCourseAccessEvidence: ParticipantAccessEvidence.Unknown),
            new CourseParticipantSummary("never", "Outro estudante sintético", null, false,
                null, null, null, [], [], LastCourseAccessEvidence: ParticipantAccessEvidence.Never),
        };
        var handler = new GenerateCourseOverviewQueryHandler(
            new ParticipantsGateway(students), new CurrentUserGateway());

        var result = await handler.Handle(new GenerateCourseOverviewQuery("course-1"), CancellationToken.None);

        Assert.Equal(0, result.StudentsWhoAccessed);
        Assert.Equal(1, result.StudentsNeverAccessed);
        Assert.Equal(1, result.StudentsAccessUnknown);
        Assert.False(result.AccessCoverageAvailable);
        Assert.Contains(result.SuggestedActionsForTutor,
            action => action.Contains("Cobertura de acesso incompleta", StringComparison.Ordinal));
        Assert.DoesNotContain(result.SuggestedActionsForTutor,
            action => action.Contains("Acesso da turma satisfatório", StringComparison.Ordinal));
    }

    private sealed class ParticipantsGateway(IReadOnlyList<CourseParticipantSummary> students) : IMoodleParticipantsGateway
    {
        public Task<CourseParticipantsPage> GetCourseParticipantsAsync(
            string userExternalId, string courseId, ParticipantStatusFilter statusFilter,
            int page, int pageSize, bool studentsOnly, bool includeEmail, string? groupId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CourseParticipantsPage(courseId, page, pageSize, statusFilter,
                studentsOnly, includeEmail, false, students));

        public Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(
            string userExternalId, string courseId, int maxCandidates, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
            string userExternalId, string courseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CourseGroupSummary>>([]);
    }

    private sealed class CurrentUserGateway : IMoodleCurrentUserIdGateway
    {
        public Task<long> GetCurrentUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(1L);
    }
}
