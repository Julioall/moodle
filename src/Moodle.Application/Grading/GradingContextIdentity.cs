using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

internal static class GradingContextIdentity
{
    public static bool EnsureVersioned(
        AssistedGradingItem item,
        GradingContextSnapshotDocument? snapshot)
    {
        if (NeedsRestore(item) && snapshot is not null)
        {
            item.RestoreContextSnapshotIdentity(snapshot);
        }

        return item.ContextVersion is > 0 &&
            !string.IsNullOrWhiteSpace(item.ContextHash) &&
            !string.IsNullOrWhiteSpace(item.ContextStatus) &&
            !string.Equals(item.ContextStatus, "blocked", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(item.ContextStatus, "legacy_unversioned", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsRestore(AssistedGradingItem item) =>
        item.ContextVersion is null or <= 0 ||
        string.IsNullOrWhiteSpace(item.ContextHash) ||
        string.IsNullOrWhiteSpace(item.ContextStatus) ||
        string.Equals(item.ContextStatus, "legacy_unversioned", StringComparison.OrdinalIgnoreCase);
}