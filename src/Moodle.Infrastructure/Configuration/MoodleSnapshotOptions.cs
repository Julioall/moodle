using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure.Configuration;

public sealed class MoodleSnapshotOptions
{
    public const string SectionName = "MoodleSnapshots";

    public int QueueCapacity { get; init; } = 256;
    // Moodle installations often protect the REST endpoint with a small
    // worker pool. Keep the default conservative; operators can raise it per
    // environment after observing the upstream error rate.
    public int GlobalConcurrency { get; init; } = 2;
    public int PerConnectionConcurrency { get; init; } = 1;
    public int FeedbackReadConcurrency { get; init; } = 1;
    public bool BulkGradebookEnabled { get; init; } = true;
    public int MaxBulkGradebookStudents { get; init; } = 200;
    public int MaxBulkGradebookCells { get; init; } = 20_000;
    public int IndividualGradebookConcurrency { get; init; } = 2;
    public int GradebookFreshMinutes { get; init; } = 15;
    public int GradebookStaleMinutes { get; init; } = 120;
    // Course end is not a gradebook immutability signal. Keep accepting
    // post-course recovery/grading updates for this window before allowing a
    // gradebook snapshot to become immutable.
    public int GradebookFreezeAfterCourseEndDays { get; init; } = 30;
    public int MaxAnalyticalSnapshotSkewMinutes { get; init; } = 15;
    // Keep interactive course listing responsive when a stale snapshot needs
    // a bounded live verification against a slow Moodle installation.
    public int CourseLiveFallbackTimeoutSeconds { get; init; } = 8;
    public int CourseSnapshotResolveTimeoutSeconds { get; init; } = 2;
    public int CourseSnapshotQueueTimeoutSeconds { get; init; } = 2;
    public int MaxPayloadBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxCoursePayloadBytes { get; init; } = 32 * 1024 * 1024;
    public int CoursePageSize { get; init; } = 250;
    // Keep enough headroom for large institutional catalogues. The worker
    // still reports an incomplete snapshot if this safety cap is reached.
    public int MaxCoursePages { get; init; } = 100;
    public int ParticipantPageSize { get; init; } = 1000;
    public int MaxParticipantPages { get; init; } = 100;
    public int AssignmentBatchSize { get; init; } = 100;
    public int AssignmentGradeBatchSize { get; init; } = 50;
    public int RunRetentionDays { get; init; } = 30;
    public int CleanupIntervalMinutes { get; init; } = 60;
    public int QueueStarvationHours { get; init; } = 24;
    public int LeaseMinutes { get; init; } = 30;

    public MoodleSnapshotOptions Normalize() => new()
    {
        QueueCapacity = Math.Clamp(QueueCapacity, 16, 10_000),
        GlobalConcurrency = Math.Clamp(GlobalConcurrency, 1, 64),
        PerConnectionConcurrency = Math.Clamp(PerConnectionConcurrency, 1, 16),
        FeedbackReadConcurrency = Math.Clamp(FeedbackReadConcurrency, 1, 16),
        BulkGradebookEnabled = BulkGradebookEnabled,
        MaxBulkGradebookStudents = Math.Clamp(MaxBulkGradebookStudents, 1, 10_000),
        MaxBulkGradebookCells = Math.Clamp(MaxBulkGradebookCells, 1, 1_000_000),
        IndividualGradebookConcurrency = Math.Clamp(IndividualGradebookConcurrency, 1, 32),
        GradebookFreshMinutes = Math.Clamp(GradebookFreshMinutes, 1, 24 * 60),
        GradebookStaleMinutes = Math.Clamp(GradebookStaleMinutes, 1, 7 * 24 * 60),
        GradebookFreezeAfterCourseEndDays = Math.Clamp(GradebookFreezeAfterCourseEndDays, 1, 3650),
        MaxAnalyticalSnapshotSkewMinutes = Math.Clamp(MaxAnalyticalSnapshotSkewMinutes, 1, 24 * 60),
        CourseLiveFallbackTimeoutSeconds = Math.Clamp(CourseLiveFallbackTimeoutSeconds, 1, 30),
        CourseSnapshotResolveTimeoutSeconds = Math.Clamp(CourseSnapshotResolveTimeoutSeconds, 1, 10),
        CourseSnapshotQueueTimeoutSeconds = Math.Clamp(CourseSnapshotQueueTimeoutSeconds, 1, 10),
        MaxPayloadBytes = Math.Clamp(MaxPayloadBytes, 64 * 1024, 100 * 1024 * 1024),
        MaxCoursePayloadBytes = Math.Clamp(MaxCoursePayloadBytes, 1024 * 1024, 256 * 1024 * 1024),
        CoursePageSize = Math.Clamp(CoursePageSize, 1, 1000),
        MaxCoursePages = Math.Clamp(MaxCoursePages, 1, 100),
        ParticipantPageSize = Math.Clamp(ParticipantPageSize, 1, 1000),
        MaxParticipantPages = Math.Clamp(MaxParticipantPages, 1, 1000),
        AssignmentBatchSize = Math.Clamp(AssignmentBatchSize, 1, 500),
        // Moodle's mod_assign_get_grades contract is safely bounded at 50
        // assignment IDs per request; callers may choose a smaller canary.
        AssignmentGradeBatchSize = Math.Clamp(AssignmentGradeBatchSize, 1, 50),
        RunRetentionDays = Math.Clamp(RunRetentionDays, 1, 3650),
        CleanupIntervalMinutes = Math.Clamp(CleanupIntervalMinutes, 5, 24 * 60),
        QueueStarvationHours = Math.Clamp(QueueStarvationHours, 1, 24 * 30),
        LeaseMinutes = Math.Clamp(LeaseMinutes, 5, 180),
    };

    public int GetPayloadLimit(string dataset) =>
        string.Equals(dataset, MoodleSnapshotDatasets.Courses, StringComparison.OrdinalIgnoreCase)
            ? MaxCoursePayloadBytes
            : MaxPayloadBytes;
}
