using Moodle.Application.Abstractions;

namespace Moodle.Application.MoodleApi;

/// <summary>
/// Shares read-attempt budgets across transport sends and retries of the same
/// logical read. Separate Moodle calls inside a batch keep separate budgets.
/// </summary>
public static class MoodleReadAttemptBudgetScope
{
    private static readonly AsyncLocal<InvocationFrame?> Current = new();

    public static bool IsActive => Current.Value is not null;

    /// <summary>Begins a retry invocation, nesting it under the current read when present.</summary>
    public static IDisposable BeginInvocation()
    {
        var previous = Current.Value;
        var frame = previous is null
            ? new InvocationFrame(new BudgetRegistry(), "root")
            : previous.CreateChildInvocation();
        Current.Value = frame;
        return new RestoreFrame(previous);
    }

    /// <summary>Begins one logical helper attempt while retaining budgets from earlier attempts.</summary>
    public static IDisposable BeginAttempt()
    {
        var previous = Current.Value;
        if (previous is null) return EmptyScope.Instance;
        Current.Value = previous.CreateAttemptFrame();
        return new RestoreFrame(previous);
    }

    /// <summary>
    /// Returns the budget assigned to this call within the active retry scope,
    /// or null when the call is not being retried by <see cref="MoodleReadRetry"/>.
    /// The call key should be an opaque, stable identifier for the read.
    /// </summary>
    public static MoodleAttemptBudget? GetBudget(string callKey, int maximumAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callKey);
        if (maximumAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        var frame = Current.Value;
        if (frame is null) return null;

        var occurrence = frame.TakeCallOccurrence(callKey);
        return frame.Registry.GetBudget(frame.Path, callKey, occurrence, maximumAttempts);
    }

    /// <summary>Reports whether the active invocation registered a transport-attempt budget.</summary>
    public static bool HasBudgetForCurrentInvocation() =>
        Current.Value is { } frame && frame.Registry.HasBudget(frame.Path);

    private sealed class InvocationFrame(BudgetRegistry registry, string path)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _callOccurrences = new(StringComparer.Ordinal);
        private int _childInvocations;

        public BudgetRegistry Registry { get; } = registry;
        public string Path { get; } = path;

        public InvocationFrame CreateAttemptFrame() => new(Registry, Path);

        public InvocationFrame CreateChildInvocation()
        {
            int childIndex;
            lock (_gate) childIndex = _childInvocations++;
            return new InvocationFrame(Registry, $"{Path}/{childIndex}");
        }

        public int TakeCallOccurrence(string callKey)
        {
            lock (_gate)
            {
                _callOccurrences.TryGetValue(callKey, out var occurrence);
                _callOccurrences[callKey] = occurrence + 1;
                return occurrence;
            }
        }
    }

    private sealed class BudgetRegistry
    {
        private readonly object _gate = new();
        private readonly Dictionary<BudgetKey, MoodleAttemptBudget> _budgets = [];

        public MoodleAttemptBudget GetBudget(
            string path,
            string callKey,
            int occurrence,
            int maximumAttempts)
        {
            lock (_gate)
            {
                var key = new BudgetKey(path, callKey, occurrence, maximumAttempts);
                if (_budgets.TryGetValue(key, out var existing)) return existing;

                var budget = new MoodleAttemptBudget(maximumAttempts);
                _budgets.Add(key, budget);
                return budget;
            }
        }

        public bool HasBudget(string path)
        {
            lock (_gate)
                return _budgets.Keys.Any(key => string.Equals(key.Path, path, StringComparison.Ordinal));
        }
    }

    private readonly record struct BudgetKey(string Path, string CallKey, int Occurrence, int MaximumAttempts);

    private sealed class RestoreFrame(InvocationFrame? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Current.Value = previous;
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}
