using Moodle.Domain;
using Moodle.Application.MoodleApi;

namespace Moodle.Application.Abstractions;

public interface IMoodleAssignmentSubmissionsGateway
{
    Task<IReadOnlyList<AssignmentExtensionsBatch>> GetAssignmentExtensionsBatchAsync(
        string userExternalId, IReadOnlyCollection<string> assignmentIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AssignmentExtensionsBatch>>([]);

    Task<IReadOnlyList<AssignmentEffectiveDeadlinesBatch>> GetAssignmentEffectiveDeadlinesBatchAsync(
        string userExternalId,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> assignmentUserIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AssignmentEffectiveDeadlinesBatch>>([]);

    Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
        string userExternalId,
        IReadOnlyCollection<string> assignmentIds,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        return GetAssignmentSubmissionsBatchFallbackAsync(
            userExternalId,
            assignmentIds,
            status,
            since,
            before,
            cancellationToken);
    }

    Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
        string userExternalId,
        string assignmentId,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before,
        CancellationToken cancellationToken);

    private async Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchFallbackAsync(
        string userExternalId,
        IReadOnlyCollection<string> assignmentIds,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        const int maxConcurrentFallbackReads = 4;
        using var gate = new SemaphoreSlim(maxConcurrentFallbackReads, maxConcurrentFallbackReads);
        var results = await Task.WhenAll(assignmentIds.Select(async assignmentId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var submissions = await GetAssignmentSubmissionsAsync(
                    userExternalId,
                    assignmentId,
                    status,
                    since,
                    before,
                    cancellationToken);
                return new AssignmentSubmissionsBatch(assignmentId, submissions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failure = MoodleErrorContract.Describe(exception);
                return new AssignmentSubmissionsBatch(
                    assignmentId,
                    [],
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
}

public sealed record AssignmentSubmissionsBatch(
    string AssignmentId,
    IReadOnlyList<AssignmentSubmissionRecord> Submissions,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// Complete is true only for an observed full user-flags response for this assignment.
/// It does not certify that group overrides were read.
/// </summary>
public sealed record AssignmentExtensionsBatch(
    string AssignmentId,
    IReadOnlyDictionary<string, DateTimeOffset?> ExtensionDueDates,
    bool Complete,
    string? ErrorCode = null)
{
    public bool TryGetExtension(string userId, out DateTimeOffset? extensionDueAt)
    {
        ExtensionDueDates.TryGetValue(userId, out extensionDueAt);
        // A complete flags list with no user record proves absence of an extension.
        return Complete;
    }
}

/// <summary>
/// Per-user effective assignment deadlines returned by Moodle after resolving
/// user and group overrides. A known null DueAt means Moodle confirmed that no
/// deadline applies; an absent or unknown entry is incomplete evidence.
/// </summary>
public sealed record AssignmentEffectiveDeadlinesBatch(
    string AssignmentId,
    IReadOnlyDictionary<string, AssignmentEffectiveDeadline> DeadlinesByUser,
    bool Complete,
    string? ErrorCode = null)
{
    public bool TryGetDeadline(string userId, out AssignmentEffectiveDeadline deadline) =>
        DeadlinesByUser.TryGetValue(userId, out deadline!);
}

public sealed record AssignmentEffectiveDeadline(
    DateTimeOffset? DueAt,
    bool Known);
