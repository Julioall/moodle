namespace Moodle.Application.Abstractions;

public interface IGradingAnalysisService
{
    /// <summary>
    /// Analisa uma submissao de estudante e gera sugestao de nota, feedback e evidencias por criterio.
    /// Retorna bloqueio estruturado quando a base for insuficiente para sugerir nota confiavel.
    /// </summary>
    Task<GradingAnalysisResult> AnalyzeAsync(
        GradingAnalysisRequest request,
        CancellationToken cancellationToken);
}

public sealed record GradingAnalysisRequest(
    string AssignmentName,
    decimal MaxGrade,
    string? ActivityDescription,
    string? RubricOrCriteria,
    string? TeacherInstructions,
    string SubmissionText,
    IReadOnlyList<string> FileHashes,
    string? ContextHash = null);

public sealed record GradingAnalysisResult(
    decimal? SuggestedGrade,
    decimal Confidence,
    string AnalysisStatus,
    string? FeedbackToStudent,
    string? PrivateNotesToTeacher,
    IReadOnlyList<GradingCriterionAnalysis> CriterionAnalysis,
    IReadOnlyList<string> Blocks);

public sealed record GradingCriterionAnalysis(
    string? CriterionId,
    string CriterionText,
    decimal? MaxPoints,
    decimal? SuggestedPoints,
    string? EvidenceFound,
    string? Gaps,
    bool TeacherReviewRequired);

public static class AnalysisStatus
{
    public const string Draft = "draft";
    public const string AwaitingAiAnalysis = "awaiting_ai_analysis";
    public const string BlockedMissingCriteria = "blocked_missing_criteria";
    public const string BlockedEmptySubmission = "blocked_empty_submission";
    public const string BlockedUnknownScale = "blocked_unknown_scale";
    public const string BlockedUnreadableFile = "blocked_unreadable_file";
}
