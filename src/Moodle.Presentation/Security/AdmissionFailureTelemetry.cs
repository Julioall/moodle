namespace Moodle.Presentation.Security;

/// <summary>
/// Carries a bounded failure code from an admission boundary to the outer
/// request metrics middleware without adding identifiers or request data.
/// </summary>
internal static class AdmissionFailureTelemetry
{
    private static readonly object FailureKindKey = new();

    public static void MarkConnectorRateLimited(HttpContext context) =>
        context.Items[FailureKindKey] = "connector_rate_limited";

    public static void MarkMcpRateLimited(HttpContext context) =>
        context.Items[FailureKindKey] = "mcp_rate_limited";

    public static void MarkPreviewBusy(HttpContext context) =>
        context.Items[FailureKindKey] = "preview_busy";

    public static string? GetFailureKind(HttpContext context) =>
        context.Items.TryGetValue(FailureKindKey, out var value) ? value as string : null;
}
