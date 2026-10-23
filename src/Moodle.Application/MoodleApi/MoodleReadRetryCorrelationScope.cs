namespace Moodle.Application.MoodleApi;

/// <summary>
/// Correlates separate REST calls made by one logical read-retry operation.
/// Each REST call still has its own call ID; this ID only groups those calls.
/// </summary>
public static class MoodleReadRetryCorrelationScope
{
    private static readonly AsyncLocal<GroupState?> Ambient = new();

    public static string? RetryGroupId => Ambient.Value?.RetryGroupId;

    public static bool HasPriorTransientFailure => Ambient.Value?.HasPriorTransientFailure == true;

    internal static IDisposable BeginInvocation()
    {
        var previous = Ambient.Value;
        if (previous is not null)
        {
            return NoopScope.Instance;
        }

        Ambient.Value = new GroupState(Guid.NewGuid().ToString("N"));
        return new RestoreScope(previous);
    }

    internal static void RecordTransientFailure()
    {
        if (Ambient.Value is { } current)
        {
            current.HasPriorTransientFailure = true;
        }
    }

    private sealed class GroupState(string retryGroupId)
    {
        public string RetryGroupId { get; } = retryGroupId;
        public bool HasPriorTransientFailure { get; set; }
    }

    private sealed class RestoreScope(GroupState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();
        public void Dispose() { }
    }
}
