using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Application.Submissions;

public static class AssignmentSubmissionLatenessProjection
{
    public static (DateTimeOffset? DueAt, bool Known) Deadline(
        CourseActivitySummary activity, AssignmentSettingsSummary? settings) =>
        settings?.DueAtKnown == true &&
        string.Equals(settings.AssignmentId, activity.InstanceId ?? activity.ActivityId, StringComparison.Ordinal)
            ? (settings.DueAt, true) : (activity.DueAt, activity.DueAtKnown);

    public static AssignmentSubmissionSummary Apply(AssignmentSubmissionSummary row,
        DateTimeOffset? dueAt, bool dueAtKnown, AssignmentExtensionsBatch? extensions,
        AssignmentEffectiveDeadlinesBatch? effectiveDeadlines = null)
    {
        var isSubmitted = string.Equals(row.Status, "submitted", StringComparison.OrdinalIgnoreCase);
        var effectiveDueAt = dueAt;
        var effectiveDueAtKnown = dueAtKnown;
        var dueDateSource = "assignment_due";
        if (isSubmitted)
        {
            // A base assignment date cannot prove the effective date for a
            // submitted student while user/group overrides remain unresolved.
            if (effectiveDeadlines is not null &&
                effectiveDeadlines.TryGetDeadline(row.UserId, out var effectiveDeadline) &&
                effectiveDeadline.Known)
            {
                effectiveDueAt = effectiveDeadline.DueAt;
                effectiveDueAtKnown = true;
                dueDateSource = "moodle_effective_user_due";
            }
            else
            {
                effectiveDueAtKnown = false;
            }
        }

        DateTimeOffset? extension = null;
        var known = extensions?.TryGetExtension(row.UserId, out extension) == true;
        var lateness = SubmissionLatenessResolver.Resolve(
            row.Status, row.ModifiedAt, row.SubmittedAt, effectiveDueAt, effectiveDueAtKnown, extension, known,
            dueDateSource);
        return SubmissionLatenessResolver.Apply(row, lateness) with
        {
            LatenessCoverageComplete = !isSubmitted || effectiveDueAtKnown && known
        };
    }

    public static async Task<IReadOnlyList<AssignmentExtensionsBatch>> ReadExtensionsAsync(
        IMoodleAssignmentSubmissionsGateway gateway, string userExternalId,
        IReadOnlyCollection<string> assignmentIds, CancellationToken cancellationToken)
    {
        if (assignmentIds.Count == 0) return [];
        try { return await gateway.GetAssignmentExtensionsBatchAsync(userExternalId, assignmentIds, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    public static async Task<IReadOnlyList<AssignmentEffectiveDeadlinesBatch>> ReadEffectiveDeadlinesAsync(
        IMoodleAssignmentSubmissionsGateway gateway,
        string userExternalId,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> assignmentUserIds,
        CancellationToken cancellationToken)
    {
        if (assignmentUserIds.Count == 0) return [];
        try
        {
            return await gateway.GetAssignmentEffectiveDeadlinesBatchAsync(
                userExternalId, assignmentUserIds, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> EffectiveDeadlineRequests(
        IEnumerable<AssignmentSubmissionsBatch> submissions)
    {
        return submissions
            .Where(batch => string.IsNullOrWhiteSpace(batch.ErrorCode))
            .ToDictionary(
                batch => batch.AssignmentId,
                batch => (IReadOnlyCollection<string>)batch.Submissions
                    .Where(submission => string.Equals(submission.Status, "submitted", StringComparison.OrdinalIgnoreCase))
                    .Select(submission => submission.UserId)
                    .Where(userId => !string.IsNullOrWhiteSpace(userId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }
}
