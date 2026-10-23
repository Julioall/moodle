namespace Moodle.Domain;

public enum AssignmentSubmissionFilter
{
    All = 0,
    Submitted = 1,
    NotSubmitted = 2,
    Late = 3,
    NeedsGrading = 4
}

public sealed record AssignmentSubmissionsPage(
    string CourseId,
    string AssignmentId,
    string? AssignmentModuleId,
    string AssignmentName,
    int Page,
    int PageSize,
    AssignmentSubmissionFilter Filter,
    bool IncludeLate,
    bool IncludeUngraded,
    DateTimeOffset? Since,
    DateTimeOffset? Before,
    int Total,
    bool HasMore,
    IReadOnlyList<AssignmentSubmissionSummary> Submissions)
{
    public DateTimeOffset? OpenAt { get; init; }
    public bool IsFutureActivity { get; init; }
    public int LatenessUnknownCount { get; init; }
    /// <summary>
    /// True only when every applicable effective deadline source, including group overrides, was covered.
    /// </summary>
    public bool LatenessCoverageComplete { get; init; }
    public int EvaluationUnknownCount { get; init; }
    public bool EvaluationCoverageComplete { get; init; }
}

public sealed record AssignmentSubmissionSummary(
    string UserId,
    string? FullName,
    string? SubmissionId,
    string Status,
    string? GradingStatus,
    bool Submitted,
    bool Late,
    bool NeedsGrading,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ModifiedAt,
    int? AttemptNumber,
    int FileCount,
    bool HasOnlineText,
    IReadOnlyList<AssignmentSubmissionFile>? Files = null,
    decimal? CurrentGrade = null,
    string? CurrentFeedback = null,
    decimal? GradeMax = null,
    SubmissionEvaluationState EvaluationState = SubmissionEvaluationState.Unknown,
    string? OnlineText = null,
    long? CurrentGraderId = null,
    long? CurrentGradeTimeModified = null)
{
    // Old persisted snapshots deserialize to unknown; their legacy bool is not evidence.
    public string LatenessState { get; init; } = "unknown";
    public string LatenessReason { get; init; } = "legacy_or_missing_evidence";
    public DateTimeOffset? EffectiveDueAt { get; init; }
    public string DeadlineSource { get; init; } = "unavailable";
    public string SubmissionTimestampSource { get; init; } = "unavailable";
    public bool LatenessCoverageComplete { get; init; }
}

public sealed record AssignmentSubmissionFile(
    string Filename,
    string? MimeType,
    long? SizeBytes,
    string FileUrl);

public sealed record AssignmentSubmissionRecord(
    string SubmissionId,
    string UserId,
    string Status,
    string? GradingStatus,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ModifiedAt,
    int? AttemptNumber,
    int FileCount,
    bool HasOnlineText,
    IReadOnlyList<AssignmentSubmissionFile>? Files = null,
    string? CurrentFeedback = null,
    string? OnlineText = null,
    long? CurrentGraderId = null,
    long? CurrentGradeTimeModified = null);
