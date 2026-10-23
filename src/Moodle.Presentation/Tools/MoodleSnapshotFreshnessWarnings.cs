using Moodle.Application.Abstractions;

namespace Moodle.Presentation.Tools;

internal static class MoodleSnapshotFreshnessWarnings
{
    public const string SnapshotSkewDataset = "snapshot_skew";

    public static bool HasSnapshotSkew(CourseReadSnapshotMetadata metadata) =>
        metadata.IncompleteDatasets.Contains(SnapshotSkewDataset, StringComparer.OrdinalIgnoreCase);

    public static bool IsUnsafeForSnapshotDecision(CourseReadSnapshotMetadata metadata) =>
        metadata.MissingDatasets.Count > 0 ||
        metadata.IncompleteDatasets.Count > 0 ||
        metadata.StaleDatasets.Count > 0;

    public static bool IsDecisionSafe(bool complete, bool stale) => complete && !stale;

    public static IReadOnlyList<string> BuildWarnings(CourseReadSnapshotMetadata metadata)
    {
        var warnings = metadata.IncompleteDatasets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(dataset =>
                $"O snapshot '{dataset}' esta incompleto; a resposta pode combinar snapshot e leitura live.")
            .ToList();

        if (!metadata.IsComplete && warnings.Count == 0)
        {
            warnings.Add("O snapshot do curso esta incompleto; a resposta pode combinar snapshot e leitura live.");
        }

        return warnings;
    }
}
