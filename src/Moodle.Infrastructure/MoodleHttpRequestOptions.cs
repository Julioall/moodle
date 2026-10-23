using System.Net.Http;

namespace Moodle.Infrastructure;

/// <summary>
/// Per-request controls shared by the Moodle REST client and its resilience
/// policies. A write must opt out of automatic retries because a lost response
/// can mean that Moodle already applied the mutation.
/// </summary>
internal static class MoodleHttpRequestOptions
{
    public static readonly HttpRequestOptionsKey<bool> DisableAutomaticRetry =
        new("Moodle.DisableAutomaticRetry");

    public static readonly HttpRequestOptionsKey<bool> EnableMcpReadRateLimitRetry =
        new("Moodle.EnableMcpReadRateLimitRetry");

    public static readonly HttpRequestOptionsKey<bool> McpReadRateLimitRetryUsed =
        new("Moodle.McpReadRateLimitRetryUsed");

    public static readonly HttpRequestOptionsKey<string> FunctionName =
        new("Moodle.FunctionName");

    public static readonly HttpRequestOptionsKey<string> OperationKind =
        new("Moodle.OperationKind");

    public static readonly HttpRequestOptionsKey<string> ConnectionAlias =
        new("Moodle.ConnectionAlias");

    public static readonly HttpRequestOptionsKey<int> TransportAttempt =
        new("Moodle.TransportAttempt");
}
