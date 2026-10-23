namespace Moodle.Domain;

/// <summary>
/// Durable, course-scoped snapshot of assignment submissions. The snapshot
/// stores normalized rows rather than raw Moodle payloads so read tools can
/// apply filters and pagination without contacting Moodle again.
/// </summary>
public sealed record CourseAssignmentSubmissionsSnapshot(
    string CourseId,
    IReadOnlyList<AssignmentSubmissionsSnapshotItem> Assignments);

public sealed record AssignmentSubmissionsSnapshotItem(
    string AssignmentId,
    string? AssignmentModuleId,
    string AssignmentName,
    DateTimeOffset? DueAt,
    IReadOnlyList<AssignmentSubmissionSummary> Submissions,
    bool IsComplete = true,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    decimal? MaxGrade = null,
    bool? IsGradable = null,
    AssignmentGradingMode GradingMode = AssignmentGradingMode.Unknown,
    AssignmentSnapshotCoverage? Coverage = null,
    DateTimeOffset? ReadAt = null)
{
    /// <summary>True when every submitted student has a resolved effective deadline and extension coverage.</summary>
    public bool LatenessCoverageComplete { get; init; }
}

public enum AssignmentGradingMode
{
    Unknown = 0,
    Numeric = 1,
    Scale = 2,
    FeedbackOnly = 3
}

public sealed record AssignmentSnapshotCoverage(
    bool ParticipantsComplete,
    bool SubmissionsComplete,
    bool ConfigurationComplete,
    bool GradesComplete,
    DateTimeOffset ObservedAt)
{
    public bool NeedsGradingComplete =>
        ParticipantsComplete && SubmissionsComplete && ConfigurationComplete && GradesComplete;
}
