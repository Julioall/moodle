using Moodle.Domain;

namespace Moodle.Application.Abstractions;

public sealed record MoodleSnapshotSyncRequest(
    Guid OwnerId,
    string ClientId,
    string ConnectionAlias,
    string UserExternalId,
    bool Force = false,
    string Dataset = MoodleSnapshotDatasets.Connection,
    string? CourseId = null,
    int Priority = 50,
    string? ConnectionId = null,
    string Trigger = "scheduled",
    string WorkPolicy = MoodleSnapshotWorkPolicies.Default);

public static class MoodleSnapshotWorkPolicies
{
    public const string Default = "default";
    public const string CourseOverviewStudentsV1 = "course_overview_students_v1";
    public const string CourseOverviewGradebookV1 = "course_overview_gradebook_v1";
}

public static class MoodleSnapshotDatasets
{
    public const string Connection = "connection";
    public const string Courses = "courses";
    public const string Activities = "activities";
    public const string Students = "students";
    public const string Groups = "groups";
    public const string Submissions = "submissions";
    public const string Gradebook = "gradebook";
    public const string DashboardPending = "dashboard_pending";
    public const string DashboardAccess = "dashboard_access";
}

public sealed record MoodleSnapshotEnvelope<T>(
    T Data,
    DateTimeOffset UpdatedAt,
    bool IsStale,
    bool IsFrozen,
    string Tier,
    DateTimeOffset? FreshUntil = null,
    DateTimeOffset? StaleUntil = null,
    DateTimeOffset? LastAttemptAt = null,
    string? LastError = null,
    bool IsComplete = true,
    int RecordCount = 0,
    Guid? SnapshotRunId = null);

public interface IMoodleSnapshotStore
{
    // Report reads require exact connection identity; legacy alias fallback is not eligible.
    Task<MoodleSnapshotEnvelope<T>?> GetReportSnapshotAsync<T>(Guid ownerId, string connectionId,
        string dataset, string courseId, CancellationToken cancellationToken) =>
        Task.FromResult<MoodleSnapshotEnvelope<T>?>(null);
    Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseSummary>>?> GetCoursesAsync(
        Guid ownerId,
        string connectionAlias,
        CancellationToken cancellationToken = default);

    Task<MoodleSnapshotEnvelope<CourseContentsSummary>?> GetActivitiesAsync(
        Guid ownerId,
        string connectionAlias,
        string courseId,
        CancellationToken cancellationToken = default);

    Task<MoodleSnapshotEnvelope<CourseParticipantsPage>?> GetStudentsAsync(
        Guid ownerId,
        string connectionAlias,
        string courseId,
        CancellationToken cancellationToken = default);

    Task<MoodleSnapshotEnvelope<IReadOnlyList<CourseGroupSummary>>?> GetGroupsAsync(
        Guid ownerId,
        string connectionAlias,
        string courseId,
        CancellationToken cancellationToken = default);

    Task<MoodleSnapshotEnvelope<T>?> GetAsync<T>(
        Guid ownerId,
        string connectionAlias,
        string dataset,
        string courseId = "",
        CancellationToken cancellationToken = default);

    async Task<IReadOnlyDictionary<string, MoodleSnapshotEnvelope<T>>> GetManyAsync<T>(
        Guid ownerId,
        string connectionAlias,
        string dataset,
        IReadOnlyCollection<string> courseIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, MoodleSnapshotEnvelope<T>>(StringComparer.Ordinal);
        foreach (var courseId in courseIds
                     .Where(courseId => !string.IsNullOrWhiteSpace(courseId))
                     .Select(courseId => courseId.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            var snapshot = await GetAsync<T>(ownerId, connectionAlias, dataset, courseId, cancellationToken);
            if (snapshot is not null)
            {
                result[courseId] = snapshot;
            }
        }

        return result;
    }

    Task SaveAsync<T>(
        Guid ownerId,
        string connectionAlias,
        string dataset,
        string courseId,
        T payload,
        string tier,
        bool frozen,
        bool complete,
        int recordCount,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task SaveAsync<T>(
        Guid ownerId,
        string connectionAlias,
        string dataset,
        string courseId,
        T payload,
        string tier,
        bool frozen,
        bool complete,
        int recordCount,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? snapshotRunId) =>
        SaveAsync(ownerId, connectionAlias, dataset, courseId, payload, tier, frozen, complete, recordCount, now, cancellationToken);

    void Invalidate(Guid ownerId, string connectionAlias, string dataset, string courseId = "");
}

public interface IMoodleSnapshotSyncQueue
{
    bool Enqueue(MoodleSnapshotSyncRequest request);

    Task<bool> EnqueueAsync(
        MoodleSnapshotSyncRequest request,
        CancellationToken cancellationToken = default);
}
