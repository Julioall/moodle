using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;

namespace Moodle.Application.Abstractions;

public interface IMoodleAssignmentGradeReadGateway
{
    async Task<IReadOnlyList<AssignmentGradesBatch>> GetExistingGradesBatchAsync(
        string userExternalId,
        IReadOnlyCollection<string> assignmentIds,
        IReadOnlyCollection<string> studentIds,
        CancellationToken cancellationToken)
    {
        const int maxConcurrentFallbackReads = 4;
        var normalizedAssignmentIds = assignmentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedAssignmentIds.Length == 0)
        {
            return [];
        }

        using var gate = new SemaphoreSlim(maxConcurrentFallbackReads, maxConcurrentFallbackReads);
        var results = await Task.WhenAll(normalizedAssignmentIds.Select(async assignmentId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var grades = await GetExistingGradesAsync(
                    userExternalId,
                    assignmentId,
                    studentIds,
                    cancellationToken);
                return new AssignmentGradesBatch(assignmentId, grades);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failure = MoodleErrorContract.Describe(exception);
                return new AssignmentGradesBatch(
                    assignmentId,
                    new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase),
                    failure.ErrorCode,
                    failure.Message);
            }
            finally
            {
                gate.Release();
            }
        }));

        return results;
    }

    async Task<IReadOnlyDictionary<string, AssignmentExistingGrade>> GetExistingGradesAsync(
        string userExternalId,
        string assignmentId,
        IReadOnlyCollection<string> studentIds,
        CancellationToken cancellationToken)
    {
        var grades = new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase);
        foreach (var studentId in studentIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var grade = await GetExistingGradeAsync(userExternalId, assignmentId, studentId, cancellationToken);
            if (grade is not null)
            {
                grades[studentId] = grade;
            }
        }

        return grades;
    }

    Task<AssignmentExistingGrade?> GetExistingGradeAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        CancellationToken cancellationToken);
}

public sealed record AssignmentGradesBatch(
    string AssignmentId,
    IReadOnlyDictionary<string, AssignmentExistingGrade> Grades,
    string? ErrorCode = null,
    string? ErrorMessage = null);
