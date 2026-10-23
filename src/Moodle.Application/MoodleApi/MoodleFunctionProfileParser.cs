using System.Text.Json;
using Moodle.Application.Abstractions;

namespace Moodle.Application.MoodleApi;

public static class MoodleFunctionProfileParser
{
    public static MoodleFunctionProfile Parse(
        MoodleCredentials connection,
        JsonElement payload,
        DateTimeOffset? discoveredAt = null)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("functions", out var functionsElement) ||
            functionsElement.ValueKind != JsonValueKind.Array ||
            functionsElement.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString())))
        {
            throw new MoodleApiException(
                MoodleErrorContract.InvalidResponse,
                "Moodle returned an invalid or incomplete function profile.",
                connectionId: connection.ConnectionId,
                connectionAlias: connection.Alias,
                functionName: "core_webservice_get_site_info",
                stage: MoodleIntegrationStage.ResponseParsing);
        }

        var functions = functionsElement.EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new MoodleFunctionDescriptor(
                    name,
                    MoodleFunctionClassifier.Classify(name),
                    true))
                .ToArray();

        return new MoodleFunctionProfile(
            connection.ConnectionId,
            connection.Alias,
            GetString(payload, "sitename"),
            GetString(payload, "release"),
            GetInt64(payload, "userid"),
            functions,
            discoveredAt ?? DateTimeOffset.UtcNow);
    }

    private static string? GetString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;
}
