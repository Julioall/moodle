using System.Text.Json.Nodes;
using Moodle.Domain.Benchmarking;

namespace Moodle.Application.Benchmarking;

public sealed class ParticipantComparisonProfile : IShadowComparisonProfile
{
    public string ProfileName => "course-participants";

    public ComparisonMetrics Compare(
        JsonNode? legacyResult,
        JsonNode? registryResult,
        long latencyDeltaMs,
        double payloadReductionPercent)
    {
        var missing = new List<string>();
        var extra = new List<string>();
        var differences = new List<string>();

        var legacyParticipants = ExtractParticipants(legacyResult);
        var registryParticipants = ExtractParticipants(registryResult);
        var legacyById = IndexById(legacyParticipants, "legacy", extra);
        var registryById = IndexById(registryParticipants, "registry", extra);

        foreach (var (userId, legacyParticipant) in legacyById)
        {
            if (!registryById.TryGetValue(userId, out var registryParticipant))
            {
                missing.Add($"User ID {userId} missing in registry result");
                continue;
            }

            CompareField(userId, "fullname", legacyParticipant, registryParticipant, differences);
            CompareField(userId, "suspended", legacyParticipant, registryParticipant, differences);
            CompareField(userId, "firstaccess", legacyParticipant, registryParticipant, differences);
            CompareField(userId, "lastaccess", legacyParticipant, registryParticipant, differences);
            CompareField(userId, "lastcourseaccess", legacyParticipant, registryParticipant, differences);
            CompareCollectionField(userId, "roles", legacyParticipant, registryParticipant, differences);
            CompareCollectionField(userId, "groups", legacyParticipant, registryParticipant, differences);
        }

        foreach (var userId in registryById.Keys)
        {
            if (!legacyById.ContainsKey(userId))
            {
                extra.Add($"User ID {userId} extra in registry result");
            }
        }

        var totalChecks = Math.Max(1, legacyById.Count * 5);
        var failedChecks = missing.Count * 5 + differences.Count;
        var parity = Math.Max(0, 100.0 - ((double)failedChecks / totalChecks * 100.0));

        return new ComparisonMetrics(
            SemanticParityPercent: parity,
            MissingItems: missing,
            ExtraItems: extra,
            FieldDifferences: differences,
            LatencyDeltaMs: latencyDeltaMs,
            PayloadReductionPercent: payloadReductionPercent);
    }

    private static IReadOnlyList<JsonNode> ExtractParticipants(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.Where(item => item is not null).Select(item => item!).ToArray();
        }

        if (node is JsonObject obj && obj["users"] is JsonArray users)
        {
            return users.Where(item => item is not null).Select(item => item!).ToArray();
        }

        return [];
    }

    private static Dictionary<string, JsonNode> IndexById(
        IReadOnlyList<JsonNode> participants,
        string source,
        List<string> duplicateItems)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var participant in participants)
        {
            var id = participant["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id))
            {
                duplicateItems.Add($"Participant without an id in {source} result");
                continue;
            }

            if (!result.TryAdd(id, participant))
            {
                duplicateItems.Add($"Duplicate user ID {id} in {source} result");
            }
        }

        return result;
    }

    private static void CompareField(
        string userId,
        string fieldName,
        JsonNode legacyParticipant,
        JsonNode registryParticipant,
        List<string> differences)
    {
        var legacyValue = legacyParticipant[fieldName]?.ToString();
        var registryValue = registryParticipant[fieldName]?.ToString();
        if (!string.Equals(legacyValue, registryValue, StringComparison.Ordinal))
        {
            differences.Add(
                $"User {userId} field '{fieldName}' differs: Legacy='{legacyValue}', Registry='{registryValue}'");
        }
    }

    private static void CompareCollectionField(
        string userId,
        string fieldName,
        JsonNode legacyParticipant,
        JsonNode registryParticipant,
        List<string> differences)
    {
        // Older shadow fixtures may not request these optional fields. Only
        // compare them when the legacy payload contains the field, while live
        // participant probes request both roles and groups explicitly.
        if (legacyParticipant[fieldName] is null)
        {
            return;
        }

        var legacyValue = CanonicalJson(legacyParticipant[fieldName]);
        var registryValue = CanonicalJson(registryParticipant[fieldName]);
        if (!string.Equals(legacyValue, registryValue, StringComparison.Ordinal))
        {
            differences.Add($"User {userId} field '{fieldName}' differs");
        }
    }

    private static string CanonicalJson(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => $"{property.Key}:{CanonicalJson(property.Value)}")) + "}",
        JsonArray array => "[" + string.Join(",", array
            .Select(CanonicalJson)
            .OrderBy(value => value, StringComparer.Ordinal)) + "]",
        _ => node.ToJsonString(),
    };
}
