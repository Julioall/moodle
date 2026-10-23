using Moodle.Application.Abstractions;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

internal static class AssignmentContextArtifactSelector
{
    public static async Task<(GradingArtifact? Artifact, IReadOnlyList<string> Warnings)> SelectAsync(
        IReadOnlyList<GradingArtifact> artifacts,
        string courseId,
        string assignmentId,
        string assignmentName,
        IAssignmentContextSelectionService? selectionService,
        CancellationToken cancellationToken)
    {
        var contextArtifacts = artifacts
            .Where(artifact => artifact.ArtifactType == "assignment_context" &&
                               ExtractionStatus.IsReadable(artifact.ExtractionStatus) &&
                               !string.IsNullOrWhiteSpace(artifact.ExtractedTextRef))
            .ToArray();
        if (contextArtifacts.Length == 0)
        {
            return (null, []);
        }

        if (selectionService is null)
        {
            return (LongestArtifact(contextArtifacts), []);
        }

        var candidates = contextArtifacts
            .Select((artifact, index) => new AssignmentContextCandidate(
                artifact.Id.ToString(),
                artifact.ArtifactType,
                artifact.Filename ?? $"context-{index + 1}",
                artifact.ExtractedTextRef,
                SectionNumber: null,
                DistanceFromAssignment: index))
            .ToArray();
        var selection = await selectionService.SelectAsync(
            new AssignmentContextSelectionRequest(
                courseId,
                assignmentId,
                assignmentName,
                AssignmentDescription: null,
                candidates),
            cancellationToken);
        var selected = contextArtifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Id.ToString(), selection.SelectedCandidateId, StringComparison.Ordinal));
        return (selected, selection.Warnings);
    }

    private static GradingArtifact LongestArtifact(IReadOnlyList<GradingArtifact> artifacts) =>
        artifacts.OrderByDescending(artifact => artifact.ExtractedTextRef?.Length ?? 0).First();
}
