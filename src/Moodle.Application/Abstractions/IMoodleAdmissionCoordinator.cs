namespace Moodle.Application.Abstractions;

public enum MoodleAdmissionOrigin
{
    Portal,
    Mcp,
    Snapshot,
    Report,
    Grading
}

public enum MoodleAdmissionOperation
{
    Read,
    Write,
    Token,
    Download
}

public enum MoodleAdmissionPriority
{
    Interactive,
    Background
}

public enum MoodleCooldownScope
{
    Connection,
    Installation
}

/// <summary>
/// A single logical Moodle operation. AttemptBudget is shared by every
/// transport attempt and retry belonging to this operation.
/// </summary>
public sealed record MoodleAdmissionContext(
    string OwnerId,
    string ConnectionId,
    string InstallationId,
    MoodleAdmissionOrigin Origin,
    MoodleAdmissionOperation Operation,
    DateTimeOffset DeadlineUtc,
    MoodleAttemptBudget AttemptBudget,
    MoodleAdmissionPriority Priority = MoodleAdmissionPriority.Interactive);

/// <summary>
/// Atomically counts actual HTTP sends for one logical Moodle operation.
/// Admission waits and local rejections do not consume attempts.
/// </summary>
public sealed class MoodleAttemptBudget
{
    private int _attemptsUsed;

    public MoodleAttemptBudget(int maximumAttempts)
    {
        if (maximumAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        MaximumAttempts = maximumAttempts;
    }

    public int MaximumAttempts { get; }
    public int AttemptsUsed => Volatile.Read(ref _attemptsUsed);

    /// <summary>Counts a real send while observing; Observe mode never blocks at the candidate limit.</summary>
    public int RecordObservedAttempt()
    {
        while (true)
        {
            var current = Volatile.Read(ref _attemptsUsed);
            if (current == int.MaxValue) return current;
            if (Interlocked.CompareExchange(ref _attemptsUsed, current + 1, current) == current)
                return current + 1;
        }
    }

    public bool TryConsumeAttempt()
    {
        while (true)
        {
            var current = Volatile.Read(ref _attemptsUsed);
            if (current >= MaximumAttempts) return false;
            if (Interlocked.CompareExchange(ref _attemptsUsed, current + 1, current) == current) return true;
        }
    }
}

public interface IMoodleAdmissionLease : IDisposable
{
    /// <summary>Commits the reservation immediately before one HTTP send.</summary>
    bool TryStartAttempt();
}

public interface IMoodleAdmissionCoordinator
{
    ValueTask<IMoodleAdmissionLease> AcquireAsync(
        MoodleAdmissionContext context,
        CancellationToken cancellationToken);

    void RecordRateLimit(
        MoodleAdmissionContext context,
        TimeSpan retryAfter,
        MoodleCooldownScope scope = MoodleCooldownScope.Connection);
}

/// <summary>
/// Establishes origin and priority metadata for asynchronous Moodle work that
/// runs without an HTTP request context.
/// </summary>
public interface IMoodleAdmissionWorkScope
{
    IDisposable Begin(
        MoodleAdmissionOrigin origin,
        string ownerId,
        MoodleAdmissionPriority priority,
        DateTimeOffset? deadlineUtc = null);
}

public sealed class MoodleAdmissionException(
    string errorCode,
    string message,
    TimeSpan? retryAfter = null) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
