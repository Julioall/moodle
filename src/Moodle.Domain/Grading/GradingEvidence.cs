namespace Moodle.Domain.Grading;

public sealed record GradingEvidence(
    Guid Id,
    Guid GradingItemId,
    string? CriterionId,
    string CriterionText,
    decimal? MaxPoints,
    decimal? SuggestedPoints,
    string? EvidenceText,
    string? GapsText,
    bool TeacherReviewRequired,
    DateTimeOffset CreatedAt);
