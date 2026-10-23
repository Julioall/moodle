using System.Text.Json.Nodes;
using Moodle.Domain.Benchmarking;

namespace Moodle.Application.Benchmarking;

public sealed class CourseComparisonProfile : IShadowComparisonProfile
{
    public string ProfileName => "course-list";

    public ComparisonMetrics Compare(JsonNode? legacyResult, JsonNode? registryResult, long latencyDeltaMs, double payloadReductionPercent)
    {
        var missing = new List<string>();
        var extra = new List<string>();
        var differences = new List<string>();

        var legacyCourses = ExtractCourses(legacyResult);
        var registryCourses = ExtractCourses(registryResult);

        var legacyDict = legacyCourses.ToDictionary(c => c["id"]?.ToString() ?? Guid.NewGuid().ToString());
        var registryDict = registryCourses.ToDictionary(c => c["id"]?.ToString() ?? Guid.NewGuid().ToString());

        foreach (var (id, legacyCourse) in legacyDict)
        {
            if (!registryDict.TryGetValue(id, out var registryCourse))
            {
                missing.Add($"Course ID {id} missing in registry result");
                continue;
            }

            CompareField(id, "shortname", legacyCourse, registryCourse, differences);
            CompareField(id, "fullname", legacyCourse, registryCourse, differences);
            CompareField(id, "startdate", legacyCourse, registryCourse, differences);
            CompareField(id, "enddate", legacyCourse, registryCourse, differences);

            // Legacy might map visibility differently (e.g., bool vs int), we should compare carefully.
            // Let's do string conversion for now.
            CompareField(id, "visible", legacyCourse, registryCourse, differences);
        }

        foreach (var id in registryDict.Keys)
        {
            if (!legacyDict.ContainsKey(id))
            {
                extra.Add($"Course ID {id} extra in registry result");
            }
        }

        var totalChecks = Math.Max(1, legacyDict.Count * 5); // 5 fields per course
        var failedChecks = missing.Count * 5 + differences.Count;
        var parity = Math.Max(0, 100.0 - ((double)failedChecks / totalChecks * 100.0));

        // If extra items exist, they don't necessarily reduce parity (unless we want strict exact match).
        // The user said: "Se o executor devolver mais campos, isso não é divergência."
        // We will just log them in ExtraItems.

        return new ComparisonMetrics(
            SemanticParityPercent: parity,
            MissingItems: missing,
            ExtraItems: extra,
            FieldDifferences: differences,
            LatencyDeltaMs: latencyDeltaMs,
            PayloadReductionPercent: payloadReductionPercent
        );
    }

    private static IReadOnlyList<JsonNode> ExtractCourses(JsonNode? node)
    {
        if (node == null) return Array.Empty<JsonNode>();

        // Sometimes the result is an array of courses directly, sometimes it's wrapped.
        if (node is JsonArray array)
        {
            return array.Where(n => n != null).Select(n => n!).ToList();
        }

        // Handle {"courses": [...]} wrapper if present
        if (node is JsonObject obj && obj.TryGetPropertyValue("courses", out var coursesNode) && coursesNode is JsonArray coursesArray)
        {
            return coursesArray.Where(n => n != null).Select(n => n!).ToList();
        }

        return Array.Empty<JsonNode>();
    }

    private static void CompareField(string courseId, string fieldName, JsonNode legacyCourse, JsonNode registryCourse, List<string> differences)
    {
        var legacyVal = legacyCourse[fieldName]?.ToString();
        var registryVal = registryCourse[fieldName]?.ToString();

        // Treat null and empty string equivalently in some cases if needed, but strictly for now.
        if (legacyVal != registryVal)
        {
            // Fallback for "visible" where Moodle might use "1"/"0" and legacy might use "true"/"false"
            if (fieldName == "visible")
            {
                var normLegacy = legacyVal == "true" || legacyVal == "1" ? "1" : "0";
                var normRegistry = registryVal == "true" || registryVal == "1" ? "1" : "0";
                if (normLegacy == normRegistry) return;
            }

            differences.Add($"Course {courseId} field '{fieldName}' differs: Legacy='{legacyVal}', Registry='{registryVal}'");
        }
    }
}
