using Moodle.Application.Grading;

namespace Moodle.Application.Abstractions;

public interface IMoodleAssignmentSubmissionStatusGateway
{
    Task<AssignmentSubmissionAttemptStatus?> GetSubmissionStatusAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        CancellationToken cancellationToken);

    Task<AssignmentSubmissionAttemptStatus?> GetSubmissionStatusLiveAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        CancellationToken cancellationToken) =>
        GetSubmissionStatusAsync(userExternalId, assignmentId, studentId, cancellationToken);
}
