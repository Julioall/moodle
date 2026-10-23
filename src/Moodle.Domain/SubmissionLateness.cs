namespace Moodle.Domain;

/// <summary>Lateness of the current Moodle submission version, not its first delivery.</summary>
public sealed record SubmissionLateness(
    string State, string Reason, DateTimeOffset? EffectiveDueAt,
    string DeadlineSource, string TimestampSource)
{
    public bool Late => State == "late";
    public bool Known => State != "unknown";
}

public static class SubmissionLatenessResolver
{
    public static SubmissionLateness Resolve(
        string? submissionStatus, DateTimeOffset? modifiedAt, DateTimeOffset? createdAt,
        DateTimeOffset? dueAt, bool dueAtKnown,
        DateTimeOffset? extensionDueAt, bool extensionKnown,
        string dueDateSource = "assignment_due")
    {
        var status = submissionStatus?.Trim().ToLowerInvariant();
        var timestampSource = modifiedAt.HasValue ? "moodle_submission_timemodified"
            : createdAt.HasValue ? "creation_time_proxy" : "unavailable";
        if (status is "new" or "draft" or "reopened" or "notsubmitted" or "not_submitted")
            return new("not_submitted", "submission_not_delivered", null, "not_applicable", "not_applicable");
        if (status != "submitted")
            return new("unknown", "submission_status_unknown", null, "unavailable", timestampSource);
        if (!dueAtKnown)
            return new("unknown", "assignment_deadline_unknown", null, "unavailable", timestampSource);
        if (!extensionKnown)
            return new("unknown", "individual_extension_unknown", null, dueDateSource, timestampSource);

        var effectiveDueAt = extensionDueAt.HasValue && (dueAt is null || extensionDueAt > dueAt)
            ? extensionDueAt : dueAt;
        var deadlineSource = effectiveDueAt == extensionDueAt && extensionDueAt.HasValue
            ? "individual_extension" : dueDateSource;
        if (effectiveDueAt is null)
            return new("no_deadline", "deadline_absence_observed", null, deadlineSource, timestampSource);
        // Creation time may precede both draft edits and final submission.
        if (modifiedAt is null)
            return new("unknown", "submission_timestamp_unavailable", effectiveDueAt, deadlineSource, timestampSource);
        return new(modifiedAt > effectiveDueAt ? "late" : "on_time",
            "current_submission_version", effectiveDueAt, deadlineSource, timestampSource);
    }

    public static AssignmentSubmissionSummary Apply(AssignmentSubmissionSummary row, SubmissionLateness lateness) =>
        row with
        {
            Late = lateness.Late,
            LatenessState = lateness.State,
            LatenessReason = lateness.Reason,
            EffectiveDueAt = lateness.EffectiveDueAt,
            DeadlineSource = lateness.DeadlineSource,
            SubmissionTimestampSource = lateness.TimestampSource
        };
}
