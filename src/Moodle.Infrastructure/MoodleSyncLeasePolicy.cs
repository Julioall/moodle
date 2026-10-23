namespace Moodle.Infrastructure;

public static class MoodleSyncLeasePolicy
{
    public static bool IsActive(MoodleSyncStateEntity state, DateTimeOffset now) =>
        string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase) &&
        state.LeaseUntil is { } leaseUntil &&
        leaseUntil > now;

    public static bool WasStartedBefore(MoodleSyncStateEntity state, DateTimeOffset applicationStartedAt) =>
        string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase) &&
        (state.LastStartedAt is null || state.LastStartedAt < applicationStartedAt);
}
