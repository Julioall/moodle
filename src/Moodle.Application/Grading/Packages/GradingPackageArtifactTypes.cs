namespace Moodle.Application.Grading.Packages;

internal static class GradingPackageArtifactTypes
{
    public static bool IsSubmissionFile(string? artifactType) =>
        string.Equals(artifactType, "submission_file", StringComparison.OrdinalIgnoreCase);

    public static bool IsContext(string? artifactType) =>
        string.Equals(artifactType, "assignment_context", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(artifactType, "rubric", StringComparison.OrdinalIgnoreCase);

    public static bool IsOnlineText(string? artifactType) =>
        string.Equals(artifactType, "submission_online_text", StringComparison.OrdinalIgnoreCase);

    public static bool IsOnlineTextSummary(string? summaryRef) =>
        string.Equals(summaryRef, "online_text", StringComparison.OrdinalIgnoreCase);
}
