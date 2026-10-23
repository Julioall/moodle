using System.Diagnostics;
using System.Diagnostics.Metrics;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

/// <summary>
/// Low-cardinality MCP usage metrics intended to support evidence-based
/// exposure decisions. It deliberately records no arguments or response data.
/// </summary>
public sealed class McpToolUsageMetrics : IMcpToolUsageTelemetry, IDisposable
{
    private readonly Meter _meter = new("Moodle.McpTools", "1.0.0");
    private readonly Counter<long> _invocations;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _transportRateLimitRejections;
    private readonly Histogram<double> _durationMs;
    private readonly Counter<long> _runtimePhaseCalls;
    private readonly Histogram<double> _runtimePhaseDurationMs;
    private static readonly HashSet<string> RuntimePhases = new(StringComparer.Ordinal)
    {
        "api_key_lookup", "account_lookup", "permissions", "permission_overrides", "team_memberships",
        "catalog_hit", "catalog_miss", "catalog_fetch", "catalog_coalesced", "catalog_coalesced_wait", "diagnostic_live"
    };

    public McpToolUsageMetrics()
    {
        _invocations = _meter.CreateCounter<long>("mcp_tool_invocations");
        _failures = _meter.CreateCounter<long>("mcp_tool_failures");
        _transportRateLimitRejections = _meter.CreateCounter<long>("mcp_http_rate_limit_rejections");
        _durationMs = _meter.CreateHistogram<double>("mcp_tool_duration_ms");
        _runtimePhaseCalls = _meter.CreateCounter<long>("mcp_runtime_phase_calls");
        _runtimePhaseDurationMs = _meter.CreateHistogram<double>("mcp_runtime_phase_duration_ms");
    }

    internal Meter InstrumentationMeter => _meter;

    public void RecordInvocation(
        string toolName,
        string? canonicalOperation,
        string? compatibilityAliasOf,
        string exposureProfile,
        string outcome,
        string? errorCode,
        double durationMs)
    {
        var tags = Tags(toolName, canonicalOperation, compatibilityAliasOf, exposureProfile, outcome);
        _invocations.Add(1, tags);
        _durationMs.Record(Math.Max(0, durationMs), tags);

        if (!string.Equals(outcome, "success", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("error_code", NormalizeTag(errorCode, "unknown"));
            _failures.Add(1, tags);
        }
    }

    public void Dispose() => _meter.Dispose();

    public void RecordTransportRateLimitRejection() => _transportRateLimitRejections.Add(1, new TagList
    {
        { "layer", "mcp_request_security" },
        { "http_status", 429 }
    });

    public void RecordRuntimePhase(string phase, string outcome, double durationMs)
    {
        var tags = new TagList
        {
            { "phase", RuntimePhases.Contains(phase) ? phase : "other" },
            { "outcome", outcome is "success" or "not_found" or "failure" or "canceled" or "coalesced" ? outcome : "other" }
        };
        _runtimePhaseCalls.Add(1, tags);
        _runtimePhaseDurationMs.Record(Math.Max(0, durationMs), tags);
    }

    private static TagList Tags(
        string? toolName,
        string? canonicalOperation,
        string? compatibilityAliasOf,
        string? exposureProfile,
        string? outcome) => new()
    {
        { "tool", NormalizeTag(toolName, "unknown") },
        { "canonical_operation", NormalizeTag(canonicalOperation, "unknown") },
        { "compatibility_alias_of", NormalizeTag(compatibilityAliasOf, "none") },
        { "exposure_profile", NormalizeTag(exposureProfile, "Production") },
        { "outcome", NormalizeTag(outcome, "unknown") }
    };

    private static string NormalizeTag(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var normalized = value.Trim();
        if (normalized.Length > 120)
            return "other";

        foreach (var character in normalized)
        {
            if (!(char.IsLetterOrDigit(character) || character is '.' or '_' or '-'))
                return "other";
        }

        return normalized;
    }
}
