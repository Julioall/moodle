using System.Globalization;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure;

internal static class MoodleAssignmentExtensionsReader
{
    public static async Task<IReadOnlyList<AssignmentExtensionsBatch>> ReadAsync(
        IMoodleRestClient client, MoodleCredentials credentials,
        IReadOnlyCollection<string> assignmentIds, CancellationToken cancellationToken)
    {
        var ids = assignmentIds.Where(id => long.TryParse(id, out var n) && n > 0)
            .Select(id => long.Parse(id, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal).ToArray();
        var results = new List<AssignmentExtensionsBatch>();
        foreach (var chunk in ids.Chunk(50))
        {
            var parameters = chunk.Select((id, index) => (id, index))
                .ToDictionary(pair => $"assignmentids[{pair.index}]", pair => (object?)pair.id);
            try
            {
                var payload = await MoodleReadRetry.ExecuteAsync(
                    ct => client.CallAsync(credentials, "mod_assign_get_user_flags", parameters, ct), null, cancellationToken);
                results.AddRange(Parse(payload, chunk));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                var descriptor = MoodleErrorContract.Describe(error);
                results.AddRange(chunk.Select(id => new AssignmentExtensionsBatch(id,
                    new Dictionary<string, DateTimeOffset?>(), false, descriptor.ErrorCode)));
            }
        }
        return results;
    }

    internal static IReadOnlyList<AssignmentExtensionsBatch> Parse(JsonElement payload, IReadOnlyList<string> ids)
    {
        var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        var empty = new HashSet<string>(StringComparer.Ordinal);
        var denied = new HashSet<string>(StringComparer.Ordinal);
        var ambiguousWarning = false;
        if (payload.TryGetProperty("assignments", out var assignments) && assignments.ValueKind == JsonValueKind.Array)
            foreach (var assignment in assignments.EnumerateArray())
            {
                var id = Id(assignment, "assignmentid");
                if (!entries.TryAdd(id, assignment)) duplicates.Add(id);
            }
        if (payload.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            foreach (var warning in warnings.EnumerateArray())
            {
                var id = Id(warning, "itemid");
                var code = Id(warning, "warningcode");
                if (code == "3") empty.Add(id);
                else if (ids.Contains(id)) denied.Add(id);
                else ambiguousWarning = true; // Some Moodle versions describe itemid as cmid.
            }

        return ids.Select(id =>
        {
            var dates = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
            var complete = !denied.Contains(id) && !duplicates.Contains(id) && !ambiguousWarning;
            if (entries.TryGetValue(id, out var entry) &&
                entry.TryGetProperty("userflags", out var flags) && flags.ValueKind == JsonValueKind.Array)
            {
                foreach (var flag in flags.EnumerateArray())
                {
                    var userId = Id(flag, "userid");
                    if (!long.TryParse(userId, out var user) || user <= 0 ||
                        !TryDate(flag, "extensionduedate", out var date) || !dates.TryAdd(userId, date))
                        complete = false;
                }
            }
            else complete &= !entries.ContainsKey(id) && empty.Contains(id);
            return new AssignmentExtensionsBatch(id, dates, complete,
                complete ? null : "extension_coverage_incomplete");
        }).ToArray();
    }

    private static string Id(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? property.ToString() : string.Empty;

    private static bool TryDate(JsonElement value, string name, out DateTimeOffset? date)
    {
        date = null;
        if (!long.TryParse(Id(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            return false;
        if (seconds == 0) return true;
        try { date = DateTimeOffset.FromUnixTimeSeconds(seconds); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
