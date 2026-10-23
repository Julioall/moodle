using System.Globalization;
using System.Text.Json;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure;

internal static class MoodleAssignmentEffectiveDeadlinesReader
{
    private const int MaxConcurrentParticipantReads = 4;

    public static async Task<IReadOnlyList<AssignmentEffectiveDeadlinesBatch>> ReadAsync(
        IMoodleRestClient client,
        MoodleCredentials credentials,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> assignmentUserIds,
        CancellationToken cancellationToken)
    {
        var requests = assignmentUserIds
            .Select(pair => new AssignmentRequest(pair.Key, NormalizeUserIds(pair.Value)))
            .GroupBy(request => request.AssignmentId, StringComparer.Ordinal)
            .Select(group => new AssignmentRequest(
                group.Key,
                group.SelectMany(request => request.UserIds).Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();

        var participants = requests
            .SelectMany(request => request.UserIds.Select(userId => new ParticipantRequest(request.AssignmentId, userId)))
            .ToArray();
        var reads = new ParticipantDeadlineRead[participants.Length];
        var nextIndex = -1;
        var workers = Enumerable.Range(0, Math.Min(MaxConcurrentParticipantReads, participants.Length))
            .Select(async _ =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= participants.Length)
                    {
                        return;
                    }

                    var participant = participants[index];
                    reads[index] = await ReadOneAsync(
                        client, credentials, participant.AssignmentId, participant.UserId, cancellationToken);
                }
            })
            .ToArray();
        await Task.WhenAll(workers);
        var readsByAssignment = reads
            .GroupBy(read => read.AssignmentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        return requests.Select(request =>
        {
            var userResults = request.UserIds.ToDictionary(
                userId => userId,
                userId => new AssignmentEffectiveDeadline(null, false),
                StringComparer.OrdinalIgnoreCase);
            var errorCode = (string?)null;
            if (readsByAssignment.TryGetValue(request.AssignmentId, out var assignmentReads))
            {
                foreach (var read in assignmentReads)
                {
                    userResults[read.UserId] = new AssignmentEffectiveDeadline(read.DueAt, read.Known);
                    errorCode ??= read.ErrorCode;
                }
            }

            var complete = userResults.Values.All(result => result.Known);
            return new AssignmentEffectiveDeadlinesBatch(
                request.AssignmentId,
                userResults,
                complete,
                complete ? null : errorCode ?? "effective_deadline_coverage_incomplete");
        }).ToArray();
    }

    private static async Task<ParticipantDeadlineRead> ReadOneAsync(
        IMoodleRestClient client,
        MoodleCredentials credentials,
        string assignmentId,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizePositiveId(assignmentId, out var normalizedAssignmentId) ||
            !TryNormalizePositiveId(userId, out var normalizedUserId))
        {
            return new ParticipantDeadlineRead(assignmentId, userId, null, false,
                "effective_deadline_invalid_identifier");
        }

        try
        {
            var payload = await MoodleReadRetry.ExecuteAsync(
                ct => client.CallAsync(
                    credentials,
                    "mod_assign_get_participant",
                    new Dictionary<string, object?>
                    {
                        ["assignid"] = normalizedAssignmentId,
                        ["userid"] = normalizedUserId,
                        ["embeduser"] = false
                    },
                    ct),
                null,
                cancellationToken);

            if (payload.ValueKind != JsonValueKind.Object ||
                !TryReadId(payload, "id", out var returnedUserId) ||
                returnedUserId != normalizedUserId ||
                !TryReadUnixDate(payload, "duedate", out var dueAt))
            {
                return new ParticipantDeadlineRead(assignmentId, userId, null, false,
                    "effective_deadline_response_invalid");
            }

            return new ParticipantDeadlineRead(assignmentId, userId, dueAt, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = MoodleErrorContract.Describe(exception);
            return new ParticipantDeadlineRead(assignmentId, userId, null, false, failure.ErrorCode);
        }
    }

    private static IReadOnlyList<string> NormalizeUserIds(IReadOnlyCollection<string> userIds) =>
        userIds.Select(id => TryNormalizePositiveId(id, out var normalized) ? normalized : id?.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool TryNormalizePositiveId(string? value, out string normalized)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            normalized = id.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static bool TryReadId(JsonElement payload, string propertyName, out string id)
    {
        id = string.Empty;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        var raw = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
        return TryNormalizePositiveId(raw, out id);
    }

    private static bool TryReadUnixDate(JsonElement payload, string propertyName, out DateTimeOffset? date)
    {
        date = null;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        long seconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out seconds))
        {
            // Moodle uses zero to report a confirmed absence of a deadline.
            if (seconds == 0)
            {
                return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.String &&
                 long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
        {
            if (seconds == 0)
            {
                return true;
            }
        }
        else
        {
            return false;
        }

        if (seconds < 0)
        {
            return false;
        }

        try
        {
            date = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private sealed record AssignmentRequest(string AssignmentId, IReadOnlyList<string> UserIds);
    private sealed record ParticipantRequest(string AssignmentId, string UserId);
    private sealed record ParticipantDeadlineRead(
        string AssignmentId,
        string UserId,
        DateTimeOffset? DueAt,
        bool Known,
        string? ErrorCode);
}
