using Moodle.Application.Abstractions;

namespace Moodle.Application.MoodleApi;

/// <summary>
/// Ambient marker for an eligible MCP read outside the grading tool family.
/// It enables read telemetry and opt-in resilience policies; the scope alone
/// does not enable retries.
/// </summary>
public sealed class McpReadInvocationScope(IMoodleCallTelemetry telemetry)
{
    private readonly IMoodleCallTelemetry _telemetry = telemetry;
    private static readonly AsyncLocal<Invocation?> Current = new();

    public static bool IsActive => Current.Value is not null;
    public static string? CurrentToolName => Current.Value?.ToolName;
    public static string? CurrentCanonicalOperation => Current.Value?.CanonicalOperation;

    public IDisposable Begin(string toolName, string? canonicalOperation)
    {
        var previous = Current.Value;
        var invocation = new Invocation(this, toolName, canonicalOperation, previous);
        Current.Value = invocation;
        return new Lease(invocation);
    }

    public static bool IsSafeReadTool(string? family, string? kind, string? canonicalOperation)
    {
        var normalizedFamily = family?.Trim();
        var normalizedKind = kind?.Trim();
        var normalizedOperation = canonicalOperation?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedFamily) ||
            string.IsNullOrWhiteSpace(normalizedKind) ||
            !IsReadKind(normalizedKind) ||
            normalizedKind.Contains("write", StringComparison.OrdinalIgnoreCase) ||
            normalizedFamily.Equals("grading", StringComparison.OrdinalIgnoreCase) ||
            normalizedFamily.StartsWith("grading-", StringComparison.OrdinalIgnoreCase) ||
            normalizedOperation?.StartsWith("grading.", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        return true;
    }

    private static bool IsReadKind(string kind) => kind.Equals("read", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("wrapper", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("specialized", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("cognitive", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("compatibility-alias", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("diagnostic", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("capability-discovery", StringComparison.OrdinalIgnoreCase);

    internal static void RecordLogicalRetry(Exception exception, int attempt)
    {
        var invocation = Current.Value;
        if (invocation is null)
        {
            return;
        }

        var moodleFailure = exception as MoodleApiException;
        invocation.Owner._telemetry.RecordMoodleWebServiceLogicalRetry(
            moodleFailure?.ConnectionAlias,
            moodleFailure?.FunctionName,
            invocation.ToolName,
            invocation.CanonicalOperation,
            attempt);
    }

    private sealed record Invocation(
        McpReadInvocationScope Owner,
        string ToolName,
        string? CanonicalOperation,
        Invocation? Previous);

    private sealed class Lease(Invocation invocation) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (ReferenceEquals(Current.Value, invocation))
            {
                Current.Value = invocation.Previous;
            }
        }
    }
}
