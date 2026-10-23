using Moodle.Domain;

namespace Moodle.Application.Abstractions;

[Flags]
public enum CourseReadSnapshotRequirements
{
    None = 0,
    Activities = 1 << 0,
    Students = 1 << 1,
    Groups = 1 << 2,
    Submissions = 1 << 3,
    Gradebook = 1 << 4,
}

public sealed record CourseReadSnapshotRequest(
    string CourseId,
    string? MoodleAlias,
    string UserExternalId,
    CourseReadSnapshotRequirements Requirements,
    bool AllowStale = true,
    CourseReadSnapshotPolicy Policy = CourseReadSnapshotPolicy.Default);

public enum CourseReadSnapshotPolicy
{
    Default = 0,
    CourseOverviewV1 = 1,
}

public sealed record CourseReadSnapshotMetadata(
    IReadOnlyCollection<string> RequiredDatasets,
    IReadOnlyCollection<string> MissingDatasets,
    IReadOnlyCollection<string> StaleDatasets,
    IReadOnlyCollection<string> IncompleteDatasets,
    DateTimeOffset? OldestUpdatedAt,
    DateTimeOffset? NewestUpdatedAt,
    double? SkewSeconds,
    bool IsComplete,
    bool RefreshQueued,
    bool GradebookRefreshQueued = false);

/// <summary>
/// Logical course read model composed from independent durable snapshot heads.
/// It intentionally exposes each envelope so callers can preserve freshness and
/// coverage semantics instead of treating the heads as one atomic Moodle read.
/// </summary>
public sealed record CourseReadSnapshot(
    string CourseId,
    MoodleSnapshotEnvelope<CourseContentsSummary>? Activities,
    MoodleSnapshotEnvelope<CourseParticipantsPage>? Students,
    MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>? Groups,
    MoodleSnapshotEnvelope<CourseAssignmentSubmissionsSnapshot>? Submissions,
    MoodleSnapshotEnvelope<CourseGradebookSnapshot>? Gradebook,
    CourseReadSnapshotMetadata Metadata);

public interface IMoodleCourseReadSnapshotCoordinator
{
    Task<CourseReadSnapshot?> ReadAsync(
        CourseReadSnapshotRequest request,
        CancellationToken cancellationToken = default);
}
