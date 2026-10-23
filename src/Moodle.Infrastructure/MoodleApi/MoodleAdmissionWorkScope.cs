using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure.MoodleApi;

/// <summary>
/// Carries bounded origin and owner metadata across asynchronous background
/// work without coupling Moodle gateways to a particular worker.
/// </summary>
internal sealed class MoodleAdmissionWorkScope : IMoodleAdmissionWorkScope
{
    private static readonly AsyncLocal<WorkContext?> Active = new();

    public MoodleAdmissionWorkScope() { }

    internal static WorkContext? Current => Active.Value;

    public IDisposable Begin(
        MoodleAdmissionOrigin origin,
        string ownerId,
        MoodleAdmissionPriority priority,
        DateTimeOffset? deadlineUtc = null) => BeginScope(origin, ownerId, priority, deadlineUtc);

    internal static IDisposable BeginScope(
        MoodleAdmissionOrigin origin,
        string ownerId,
        MoodleAdmissionPriority priority,
        DateTimeOffset? deadlineUtc = null)
    {
        if (origin is not (MoodleAdmissionOrigin.Snapshot or MoodleAdmissionOrigin.Report or MoodleAdmissionOrigin.Grading))
            throw new ArgumentOutOfRangeException(nameof(origin), "Background work must use a background admission origin.");
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128)
            throw new ArgumentException("A bounded owner identity is required for background Moodle work.", nameof(ownerId));

        var scope = new WorkScopeLease(Active.Value);
        Active.Value = new WorkContext(origin, ownerId, priority, deadlineUtc);
        return scope;
    }

    internal sealed record WorkContext(
        MoodleAdmissionOrigin Origin,
        string OwnerId,
        MoodleAdmissionPriority Priority,
        DateTimeOffset? DeadlineUtc);

    private sealed class WorkScopeLease(WorkContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Active.Value = previous;
        }
    }
}
