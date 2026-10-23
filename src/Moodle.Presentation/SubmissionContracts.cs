using Moodle.Domain;

namespace Moodle.Presentation;

public sealed record AppSubmissionFileDto(
    string Filename,
    string? MimeType,
    long? SizeBytes,
    string FileUrl);

public sealed record AppSubmissionDto(
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
    IReadOnlyList<AppSubmissionFileDto> Files,
    decimal? CurrentGrade,
    string? CurrentFeedback,
    decimal? GradeMax,
    string? EvaluationState = null)
{
    public string LatenessState { get; init; } = "unknown";
    public string LatenessReason { get; init; } = "legacy_or_missing_evidence";
    public DateTimeOffset? EffectiveDueAt { get; init; }
    public string DeadlineSource { get; init; } = "unavailable";
    public string SubmissionTimestampSource { get; init; } = "unavailable";
}

public sealed record AppSubmissionsPageDto(
    string CourseId,
    string AssignmentId,
    string? AssignmentModuleId,
    string AssignmentName,
    int Page,
    int PageSize,
    string Filter,
    bool IncludeLate,
    bool IncludeUngraded,
    DateTimeOffset? Since,
    DateTimeOffset? Before,
    int Total,
    bool HasMore,
    IReadOnlyList<AppSubmissionDto> Submissions)
{
    public int LatenessUnknownCount { get; init; }
    public bool LatenessCoverageComplete { get; init; }
    public int EvaluationUnknownCount { get; init; }
    public bool EvaluationCoverageComplete { get; init; }
}

public static class AppSubmissionContractMapper
{
    public static AppSubmissionDto ToDto(AssignmentSubmissionSummary item, Moodle.Application.Grading.AssignmentExistingGrade? existingGrade = null) => new(
        item.UserId,
        item.FullName,
        item.SubmissionId,
        item.Status,
        item.GradingStatus,
        item.Submitted,
        item.LatenessState == "late",
        item.NeedsGrading,
        item.SubmittedAt,
        item.ModifiedAt,
        item.AttemptNumber,
        item.FileCount,
        item.HasOnlineText,
        item.Files?.Select(file => new AppSubmissionFileDto(
            file.Filename,
            file.MimeType,
            file.SizeBytes,
            file.FileUrl)).ToArray() ?? [],
        existingGrade?.Grade ?? item.CurrentGrade,
        existingGrade?.Feedback ?? item.CurrentFeedback,
        existingGrade?.GradeMax ?? item.GradeMax,
        item.EvaluationState.ToString())
    {
        LatenessState = item.LatenessState,
        LatenessReason = item.LatenessReason,
        EffectiveDueAt = item.EffectiveDueAt,
        DeadlineSource = item.DeadlineSource,
        SubmissionTimestampSource = item.SubmissionTimestampSource
    };

    public static AppSubmissionsPageDto ToPage(AssignmentSubmissionsPage page) => new(
        page.CourseId,
        page.AssignmentId,
        page.AssignmentModuleId,
        page.AssignmentName,
        page.Page,
        page.PageSize,
        page.Filter.ToString(),
        page.IncludeLate,
        page.IncludeUngraded,
        page.Since,
        page.Before,
        page.Total,
        page.HasMore,
        page.Submissions.Select(item => ToDto(item)).ToArray())
    {
        LatenessUnknownCount = page.LatenessUnknownCount,
        LatenessCoverageComplete = page.LatenessCoverageComplete,
        EvaluationUnknownCount = page.EvaluationUnknownCount,
        EvaluationCoverageComplete = page.EvaluationCoverageComplete
    };
}
