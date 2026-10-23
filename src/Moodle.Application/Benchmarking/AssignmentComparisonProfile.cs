using System.Text.Json.Nodes;
using Moodle.Domain.Benchmarking;

namespace Moodle.Application.Benchmarking;

public sealed class AssignmentComparisonProfile : IShadowComparisonProfile
{
    public string ProfileName => "assignment-submissions";

    public ComparisonMetrics Compare(JsonNode? legacyResult, JsonNode? registryResult, long latencyDeltaMs, double payloadReductionPercent)
    {
        var missing = new List<string>();
        var extra = new List<string>();
        var differences = new List<string>();

        var legacySubmissions = ExtractSubmissions(legacyResult);
        var registrySubmissions = ExtractSubmissions(registryResult);

        var legacyDict = legacySubmissions.ToDictionary(s => s["userid"]?.ToString() ?? Guid.NewGuid().ToString());
        var registryDict = registrySubmissions.ToDictionary(s => s["userid"]?.ToString() ?? Guid.NewGuid().ToString());

        foreach (var (userId, legacySub) in legacyDict)
        {
            if (!registryDict.TryGetValue(userId, out var registrySub))
            {
                missing.Add($"User ID {userId} missing in registry result");
                continue;
            }

            CompareField(userId, "status", legacySub, registrySub, differences);
            CompareField(userId, "timecreated", legacySub, registrySub, differences);
            CompareField(userId, "timemodified", legacySub, registrySub, differences);
            CompareField(userId, "gradingstatus", legacySub, registrySub, differences);
            CompareField(userId, "attemptnumber", legacySub, registrySub, differences);
        }

        foreach (var userId in registryDict.Keys)
        {
            if (!legacyDict.ContainsKey(userId))
            {
                extra.Add($"User ID {userId} extra in registry result");
            }
        }

        var totalChecks = Math.Max(1, legacyDict.Count * 5);
        var failedChecks = missing.Count * 5 + differences.Count;
        var parity = Math.Max(0, 100.0 - ((double)failedChecks / totalChecks * 100.0));

        return new ComparisonMetrics(
            SemanticParityPercent: parity,
            MissingItems: missing,
            ExtraItems: extra,
            FieldDifferences: differences,
            LatencyDeltaMs: latencyDeltaMs,
            PayloadReductionPercent: payloadReductionPercent
        );
    }

    private static IReadOnlyList<JsonNode> ExtractSubmissions(JsonNode? node)
    {
        if (node == null) return Array.Empty<JsonNode>();

        if (node is JsonArray array)
        {
            return array.Where(n => n != null).Select(n => n!).ToList();
        }

        // Handle Moodle wrapper format: {"assignments": [{"submissions": [...]}]}
        if (node is JsonObject obj && obj.TryGetPropertyValue("assignments", out var assignmentsNode) && assignmentsNode is JsonArray assignmentsArray)
        {
            var allSubmissions = new List<JsonNode>();
            foreach (var assignment in assignmentsArray)
            {
                if (assignment is JsonObject assignmentObj && assignmentObj.TryGetPropertyValue("submissions", out var subNode) && subNode is JsonArray subArray)
                {
                    allSubmissions.AddRange(subArray.Where(n => n != null).Select(n => n!));
                }
            }
            return allSubmissions;
        }

        return Array.Empty<JsonNode>();
    }

    private static void CompareField(string userId, string fieldName, JsonNode legacySub, JsonNode registrySub, List<string> differences)
    {
        var legacyVal = legacySub[fieldName]?.ToString();
        var registryVal = registrySub[fieldName]?.ToString();

        if (legacyVal != registryVal)
        {
            differences.Add($"User {userId} field '{fieldName}' differs: Legacy='{legacyVal}', Registry='{registryVal}'");
        }
    }
}
