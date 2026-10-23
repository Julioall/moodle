using Moodle.Domain.Grading;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Projeção local, paginada e somente-leitura da revisão assistida. Esta
/// fronteira não pode depender de gateways Moodle.
/// </summary>
public interface IGradingReviewReadStore
{
    Task<GradingReviewPageReadModel> GetPageAsync(
        Guid batchJobId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed record GradingReviewPageReadModel(
    Guid BatchJobId,
    string Status,
    int TotalItems,
    int ReadyItems,
    int BlockedItems,
    int FailedItems,
    int ProgressPercent,
    int Page,
    int PageSize,
    bool HasMore,
    IReadOnlyList<GradingReviewItemReadModel> Items,
    string? CourseName = null,
    string DataSource = "local_read_model",
    string ReadModelVersion = "1",
    int QueryCount = 3);

public sealed record GradingReviewItemReadModel(
    Guid GradingItemId,
    string AssignmentId,
    string? SubmissionId,
    string StudentId,
    string? StudentName,
    string Status,
    string ReviewStatus,
    string CommitStatus,
    string? StatusReason,
    string DraftVersionHash,
    decimal? FinalGrade,
    string? FinalFeedback,
    decimal? SuggestedGrade,
    string? DraftFeedback,
    decimal? MaxGrade,
    string GradingMode,
    string? AssignmentName,
    decimal? Confidence,
    string? ContextHash,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<GradingEvidence> Evidence,
    GradingEvidenceCoverage? Coverage = null);
