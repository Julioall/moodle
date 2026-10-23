using System.Diagnostics;
using System.Text.RegularExpressions;
using Moodle.Infrastructure;

namespace Moodle.Presentation.Security;

/// <summary>Persiste somente metadados agregáveis das chamadas operacionais.</summary>
internal sealed partial class PlatformRequestMetricsMiddleware(IPlatformRequestMetricsSink metricsSink) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!ShouldRecord(context.Request.Path))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Exception? exception = null;
        try { await next(context); }
        catch (Exception ex) { exception = ex; throw; }
        finally
        {
            stopwatch.Stop();
            try
            {
                var statusCode = exception is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
                metricsSink.TryEnqueue(new PlatformRequestMetricEntity
                {
                    RecordedAtUtc = DateTimeOffset.UtcNow,
                    Method = context.Request.Method[..Math.Min(context.Request.Method.Length, 12)],
                    Endpoint = NormalizeEndpoint(context.Request.Path),
                    StatusCode = statusCode,
                    DurationMs = Math.Max(0, (long)stopwatch.Elapsed.TotalMilliseconds),
                    FailureKind = exception is not null
                        ? NormalizeFailureKind(exception.GetType().Name)
                        : AdmissionFailureTelemetry.GetFailureKind(context) ?? (statusCode >= 400 ? $"http_{statusCode}" : null)
                });
            }
            catch
            {
                // Métricas não podem afetar a resposta ou mascarar a falha original.
            }
        }
    }

    private static bool ShouldRecord(PathString path) =>
        (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)) &&
        !path.StartsWithSegments("/api/admin/metrics", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEndpoint(PathString path)
    {
        var normalized = GuidSegment().Replace(path.Value ?? "/", "{id}");
        normalized = NumericSegment().Replace(normalized, "{id}");
        return normalized.Length <= 180 ? normalized : normalized[..180];
    }

    private static string NormalizeFailureKind(string value) => value.Length <= 120 ? value : "unknown_exception";

    [GeneratedRegex(@"(?<=/)[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?=/|$)")]
    private static partial Regex GuidSegment();
    [GeneratedRegex(@"(?<=/)\d+(?=/|$)")]
    private static partial Regex NumericSegment();
}
