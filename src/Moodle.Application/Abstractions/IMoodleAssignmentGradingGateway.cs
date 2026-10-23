using Moodle.Application.Grading;

namespace Moodle.Application.Abstractions;

public interface IMoodleAssignmentGradingGateway
{
    Task<AssignmentGradeWriteResult> SaveGradeAsync(
        string userExternalId,
        AssignmentGradeWriteRequest request,
        CancellationToken cancellationToken);

    Task<AssignmentGradesBatchWriteResult> SaveGradesBatchAsync(
        string userExternalId,
        AssignmentGradesBatchWriteRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("A escrita coletiva de notas nao esta implementada neste gateway Moodle.");
}
