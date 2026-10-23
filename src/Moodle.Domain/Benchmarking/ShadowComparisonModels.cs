using System.Text.Json.Nodes;

namespace Moodle.Domain.Benchmarking;

public sealed record ShadowComparisonRequest(
    string MoodleAlias,
    string LegacyOperationName,
    string RegistryOperationName,
    Dictionary<string, object?> Arguments,
    string ComparisonProfileName);

public sealed record LegacyTrace(
    long DurationMs,
    long PayloadBytes,
    int MoodleCalls,
    JsonNode? Result);

public sealed record RegistryTrace(
    long DurationMs,
    long RawPayloadBytes,
    long NormalizedPayloadBytes,
    string PolicyDecision,
    int MoodleCalls,
    JsonNode? Result);

public sealed record ComparisonMetrics(
    double SemanticParityPercent,
    IReadOnlyList<string> MissingItems,
    IReadOnlyList<string> ExtraItems,
    IReadOnlyList<string> FieldDifferences,
    long LatencyDeltaMs,
    double PayloadReductionPercent);

public sealed record ShadowComparisonResult(
    LegacyTrace Legacy,
    RegistryTrace Registry,
    ComparisonMetrics Comparison);
