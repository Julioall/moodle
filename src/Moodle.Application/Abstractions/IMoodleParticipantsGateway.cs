using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public interface IMoodleParticipantsGateway
{
    Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken);
    Task<CourseParticipantsSample> GetCourseParticipantsSampleAsync(string userExternalId,
        string courseId, int maxCandidates, CancellationToken cancellationToken, bool includeEmail) =>
        GetCourseParticipantsSampleAsync(userExternalId, courseId, maxCandidates, cancellationToken);
    Task<CourseParticipantsPage> GetCourseParticipantsAsync(
        string userExternalId,
        string courseId,
        ParticipantStatusFilter statusFilter,
        int page,
        int pageSize,
        bool studentsOnly,
        bool includeEmail,
        string? groupId,
        CancellationToken cancellationToken);

    async Task<CourseParticipantSummary?> FindStudentAsync(
        string userExternalId,
        string courseId,
        string studentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(studentId))
        {
            return null;
        }

        var page = 1;
        while (true)
        {
            var participants = await GetCourseParticipantsAsync(
                userExternalId,
                courseId,
                ParticipantStatusFilter.Active,
                page,
                100,
                studentsOnly: true,
                includeEmail: false,
                groupId: null,
                cancellationToken);
            var student = participants.Participants.FirstOrDefault(participant =>
                string.Equals(participant.UserId, studentId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (student is not null)
            {
                return student;
            }

            if (!participants.HasMore)
            {
                return null;
            }

            page++;
        }
    }

    Task<IReadOnlyList<CourseGroupSummary>> GetCourseGroupsAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken);
}
