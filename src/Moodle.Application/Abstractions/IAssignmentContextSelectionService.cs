namespace Moodle.Application.Abstractions;

public interface IAssignmentContextSelectionService
{
    Task<AssignmentContextSelectionResult> SelectAsync(
        AssignmentContextSelectionRequest request,
        CancellationToken cancellationToken);
}

public sealed record AssignmentContextSelectionRequest(
    string CourseId,
    string AssignmentId,
    string AssignmentName,
    string? AssignmentDescription,
    IReadOnlyList<AssignmentContextCandidate> Candidates);

public sealed record AssignmentContextCandidate(
    string CandidateId,
    string SourceType,
    string Title,
    string? ExtractedText,
    int? SectionNumber,
    int? DistanceFromAssignment);

public sealed record AssignmentContextSelectionResult(
    string? SelectedCandidateId,
    string Classification,
    decimal Confidence,
    string? Reason,
    IReadOnlyList<string> SupportingCandidateIds,
    IReadOnlyList<string> Warnings);
