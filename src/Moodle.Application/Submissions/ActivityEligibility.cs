namespace Moodle.Application.Submissions;

/// <summary>
/// Shared temporal rule for submission-facing tools. An activity without an
/// opening date is eligible; an activity with a future opening date is not.
/// </summary>
internal static class ActivityEligibility
{
    public static bool IsCurrentlyOpen(DateTimeOffset? openAt, DateTimeOffset now) =>
        !openAt.HasValue || openAt.Value <= now;
}
