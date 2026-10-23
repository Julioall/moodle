namespace Moodle.Application.Abstractions;

// Selected by the backend operation, never by a report definition supplied by a user.
public sealed record GradebookReadOptions(
    bool BypassCache,
    bool EnrichScale,
    bool SingleAttempt,
    bool AllowCourseBulkRead = false)
{
    public static GradebookReadOptions Default { get; } = new(false, true, false);
    public static GradebookReadOptions Live { get; } = new(true, true, false);
    public static GradebookReadOptions LiveWithoutScale { get; } = new(true, false, false);
    public static GradebookReadOptions Preview { get; } = new(true, false, true);
    // Fresh, bounded course snapshots are used by publication preflight.
    // The gateway still makes each remote request at most once and falls
    // back only to exact reads for the requested students.
    public static GradebookReadOptions FreshBatch { get; } = new(true, false, true, true);
}
