using System.Text.Json.Nodes;
using Moodle.Domain.Benchmarking;

namespace Moodle.Application.Benchmarking;

public interface IShadowComparisonProfile
{
    string ProfileName { get; }

    /// <summary>
    /// Compares the legacy result and the registry result, calculating semantic parity metrics.
    /// </summary>
    ComparisonMetrics Compare(JsonNode? legacyResult, JsonNode? registryResult, long latencyDeltaMs, double payloadReductionPercent);
}
