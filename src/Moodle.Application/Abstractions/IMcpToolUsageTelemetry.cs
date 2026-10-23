using System.Diagnostics;

namespace Moodle.Application.Abstractions;

/// <summary>
/// Records aggregate MCP tool usage without retaining arguments, payloads,
/// credentials, user identifiers or other potentially sensitive data.
/// </summary>
public interface IMcpToolUsageTelemetry
{
    void RecordInvocation(
        string toolName,
        string? canonicalOperation,
        string? compatibilityAliasOf,
        string exposureProfile,
        string outcome,
        string? errorCode,
        double durationMs);

    void RecordTransportRateLimitRejection()
    {
    }

    void RecordRuntimePhase(string phase, string outcome, double durationMs)
    {
    }
}

public static class McpToolUsageTelemetryExtensions
{
    public static async Task<T> MeasureRuntimePhaseAsync<T>(
        this IMcpToolUsageTelemetry telemetry,
        string phase,
        Func<Task<T>> operation,
        Func<T, string>? outcomeSelector = null)
    {
        var timer = Stopwatch.StartNew();
        var outcome = "success";
        try
        {
            var result = await operation();
            if (outcomeSelector is not null) outcome = outcomeSelector(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        catch
        {
            outcome = "failure";
            throw;
        }
        finally
        {
            telemetry.RecordRuntimePhase(phase, outcome, timer.Elapsed.TotalMilliseconds);
        }
    }
}
