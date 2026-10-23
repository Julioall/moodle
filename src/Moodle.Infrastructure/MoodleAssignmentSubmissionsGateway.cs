using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain;

namespace Moodle.Infrastructure;

internal sealed class MoodleAssignmentSubmissionsGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient) : IMoodleAssignmentSubmissionsGateway
{
    private const int MaxAssignmentIdsPerRequest = 50;
    private readonly MoodleApiOptions _options = options.Value;

    public async Task<IReadOnlyList<AssignmentExtensionsBatch>> GetAssignmentExtensionsBatchAsync(
        string userExternalId, IReadOnlyCollection<string> assignmentIds, CancellationToken cancellationToken)
    {
        if (_options.UseStubData) throw new InvalidOperationException("UseStubData esta desativado para fluxos reais.");
        if (assignmentIds.Count == 0) return [];
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        return await MoodleAssignmentExtensionsReader.ReadAsync(restClient, credentials, assignmentIds, cancellationToken);
    }

    public async Task<IReadOnlyList<AssignmentEffectiveDeadlinesBatch>> GetAssignmentEffectiveDeadlinesBatchAsync(
        string userExternalId,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> assignmentUserIds,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData) throw new InvalidOperationException("UseStubData esta desativado para fluxos reais.");
        if (assignmentUserIds.Count == 0) return [];
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        return await MoodleAssignmentEffectiveDeadlinesReader.ReadAsync(
            restClient, credentials, assignmentUserIds, cancellationToken);
    }

    public async Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetAssignmentSubmissionsBatchAsync(
        string userExternalId,
        IReadOnlyCollection<string> assignmentIds,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para fluxos reais. Ajuste a configuracao para usar Moodle real.");
        }

        var normalizedAssignmentIds = assignmentIds
            .Select(id => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value.ToString(CultureInfo.InvariantCulture)
                : null)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedAssignmentIds.Length == 0)
        {
            return [];
        }

        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var result = new List<AssignmentSubmissionsBatch>(normalizedAssignmentIds.Length);

        foreach (var chunk in normalizedAssignmentIds.Chunk(MaxAssignmentIdsPerRequest))
        {
            var parameters = chunk
                .Select((assignmentId, index) => new KeyValuePair<string, object?>(
                    $"assignmentids[{index}]",
                    assignmentId))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            AddOptionalParameters(parameters, status, since, before);

            try
            {
                var payload = await MoodleReadRetry.ExecuteAsync(
                    ct => restClient.CallAsync(
                        credentials,
                        "mod_assign_get_submissions",
                        parameters,
                        ct),
                    null,
                    cancellationToken);
                var submissions = JsonSerializer.Deserialize<GetSubmissionsResponseDto>(payload.GetRawText());
                var returnedAssignments = (submissions?.Assignments ?? [])
                    .Select(assignment => new AssignmentSubmissionsBatch(
                        ToIdString(assignment.AssignmentId),
                        (assignment.Submissions ?? []).Select(ToRecord).ToArray()))
                    .ToDictionary(batch => batch.AssignmentId, StringComparer.Ordinal);

                // Moodle may reject the whole multi-assignment request when a
                // single activity is unavailable to the current role. Retry
                // omitted IDs individually so one bad Extra activity does not
                // erase valid submissions from the remaining activities.
                var missingAssignmentIds = new List<string>();
                foreach (var assignmentId in chunk)
                {
                    if (returnedAssignments.TryGetValue(assignmentId, out var batch))
                    {
                        result.Add(batch);
                    }
                    else
                    {
                        missingAssignmentIds.Add(assignmentId);
                    }
                }

                if (missingAssignmentIds.Count > 0)
                {
                    result.AddRange(await GetSingleAssignmentBatchesConcurrentlyAsync(
                        credentials,
                        missingAssignmentIds,
                        status,
                        since,
                        before,
                        cancellationToken));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A single invalid/hidden assignment can make Moodle reject
                // the complete array. Fall back to isolated calls and retain
                // a structured failure for assignments that still fail.
                result.AddRange(await GetSingleAssignmentBatchesConcurrentlyAsync(
                    credentials,
                    chunk,
                    status,
                    since,
                    before,
                    cancellationToken));
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<AssignmentSubmissionsBatch>> GetSingleAssignmentBatchesConcurrentlyAsync(
        MoodleCredentials credentials,
        IReadOnlyCollection<string> assignmentIds,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        const int maxConcurrentFallbackReads = 4;
        using var gate = new SemaphoreSlim(maxConcurrentFallbackReads, maxConcurrentFallbackReads);
        var batches = await Task.WhenAll(assignmentIds.Select(async assignmentId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await GetSingleAssignmentBatchAsync(
                    credentials,
                    assignmentId,
                    status,
                    since,
                    before,
                    cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));
        return batches;
    }

    public async Task<IReadOnlyList<AssignmentSubmissionRecord>> GetAssignmentSubmissionsAsync(
        string userExternalId,
        string assignmentId,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para fluxos reais. Ajuste a configuracao para usar Moodle real.");
        }

        var normalizedAssignmentId = ParseMoodleId(assignmentId, "assignmentId");
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);

        var batch = await GetSingleAssignmentBatchAsync(
            credentials,
            normalizedAssignmentId.ToString(CultureInfo.InvariantCulture),
            status,
            since,
            before,
            cancellationToken);
        if (batch.ErrorCode is not null)
        {
            throw new MoodleApiException(
                batch.ErrorCode,
                batch.ErrorMessage ?? MoodleErrorContract.SafeMessage(batch.ErrorCode),
                functionName: "mod_assign_get_submissions");
        }

        return batch.Submissions;
    }

    private async Task<AssignmentSubmissionsBatch> GetSingleAssignmentBatchAsync(
        MoodleCredentials credentials,
        string assignmentId,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["assignmentids[0]"] = assignmentId
        };
        AddOptionalParameters(parameters, status, since, before);

        try
        {
            var payload = await MoodleReadRetry.ExecuteAsync(
                ct => restClient.CallAsync(
                    credentials,
                    "mod_assign_get_submissions",
                    parameters,
                    ct),
                null,
                cancellationToken);
            var submissions = JsonSerializer.Deserialize<GetSubmissionsResponseDto>(payload.GetRawText());
            var assignment = (submissions?.Assignments ?? [])
                .FirstOrDefault(item => string.Equals(
                    ToIdString(item.AssignmentId),
                    assignmentId,
                    StringComparison.Ordinal));

            return assignment is null
                ? new AssignmentSubmissionsBatch(
                    assignmentId,
                    [],
                    "assignment_not_found",
                    "A tarefa nao foi encontrada ou nao esta acessivel para o usuario atual.")
                : new AssignmentSubmissionsBatch(
                    assignmentId,
                    (assignment.Submissions ?? []).Select(ToRecord).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = MoodleErrorContract.Describe(exception);
            return new AssignmentSubmissionsBatch(
                assignmentId,
                [],
                failure.ErrorCode,
                failure.Message);
        }
    }

    private static void AddOptionalParameters(
        IDictionary<string, object?> parameters,
        string? status,
        DateTimeOffset? since,
        DateTimeOffset? before)
    {
        if (!string.IsNullOrWhiteSpace(status))
        {
            parameters["status"] = status;
        }

        if (since is not null)
        {
            parameters["since"] = since.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        if (before is not null)
        {
            parameters["before"] = before.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }
    }

    private static AssignmentSubmissionRecord ToRecord(SubmissionDto dto)
    {
        var fileCount = 0;
        var hasOnlineText = false;
        var files = new List<AssignmentSubmissionFile>();
        var onlineTextParts = new List<string>();
        foreach (var plugin in dto.Plugins ?? [])
        {
            foreach (var fileArea in plugin.FileAreas ?? [])
            {
                foreach (var file in fileArea.Files ?? [])
                {
                    fileCount++;
                    var submissionFile = ToSubmissionFile(file);
                    if (submissionFile is not null)
                    {
                        files.Add(submissionFile);
                    }
                }
            }

            hasOnlineText = hasOnlineText ||
                string.Equals(plugin.Type, "onlinetext", StringComparison.OrdinalIgnoreCase) &&
                (plugin.EditorFields?.Count ?? 0) > 0;
            if (string.Equals(plugin.Type, "onlinetext", StringComparison.OrdinalIgnoreCase))
            {
                onlineTextParts.AddRange((plugin.EditorFields ?? [])
                    .Select(field => GetString(field, "text"))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text!.Trim()));
            }
        }

        return new AssignmentSubmissionRecord(
            ToIdString(dto.Id),
            ToIdString(dto.UserId),
            string.IsNullOrWhiteSpace(dto.Status) ? "unknown" : dto.Status,
            string.IsNullOrWhiteSpace(dto.GradingStatus) ? null : dto.GradingStatus,
            ToDateTimeOffset(dto.TimeCreated),
            ToDateTimeOffset(dto.TimeModified),
            ToNullableInt(dto.AttemptNumber),
            fileCount,
            hasOnlineText,
            files,
            OnlineText: onlineTextParts.Count == 0 ? null : string.Join("\n", onlineTextParts));
    }

    private static AssignmentSubmissionFile? ToSubmissionFile(JsonElement file)
    {
        var fileUrl = GetString(file, "fileurl");
        if (string.IsNullOrWhiteSpace(fileUrl))
        {
            return null;
        }

        var filename = GetString(file, "filename");
        if (string.IsNullOrWhiteSpace(filename))
        {
            filename = GetString(file, "filepath")?.Trim('/') ?? "submission-file";
        }

        return new AssignmentSubmissionFile(
            filename,
            GetString(file, "mimetype"),
            GetNullableLong(file, "filesize"),
            fileUrl);
    }

    private static int ParseMoodleId(string value, string parameterName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            return id;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico do Moodle.", parameterName);
    }

    private static string ToIdString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static int? ToNullableInt(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static long? GetNullableLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static DateTimeOffset? ToDateTimeOffset(JsonElement value)
    {
        var seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0
        };

        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    private sealed class GetSubmissionsResponseDto
    {
        [JsonPropertyName("assignments")]
        public IReadOnlyList<AssignmentDto>? Assignments { get; init; }
    }

    private sealed class AssignmentDto
    {
        [JsonPropertyName("assignmentid")]
        public JsonElement AssignmentId { get; init; }

        [JsonPropertyName("submissions")]
        public IReadOnlyList<SubmissionDto>? Submissions { get; init; }
    }

    private sealed class SubmissionDto
    {
        [JsonPropertyName("id")]
        public JsonElement Id { get; init; }

        [JsonPropertyName("userid")]
        public JsonElement UserId { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("gradingstatus")]
        public string? GradingStatus { get; init; }

        [JsonPropertyName("attemptnumber")]
        public JsonElement AttemptNumber { get; init; }

        [JsonPropertyName("timecreated")]
        public JsonElement TimeCreated { get; init; }

        [JsonPropertyName("timemodified")]
        public JsonElement TimeModified { get; init; }

        [JsonPropertyName("plugins")]
        public IReadOnlyList<SubmissionPluginDto>? Plugins { get; init; }
    }

    private sealed class SubmissionPluginDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("fileareas")]
        public IReadOnlyList<SubmissionFileAreaDto>? FileAreas { get; init; }

        [JsonPropertyName("editorfields")]
        public IReadOnlyList<JsonElement>? EditorFields { get; init; }
    }

    private sealed class SubmissionFileAreaDto
    {
        [JsonPropertyName("files")]
        public IReadOnlyList<JsonElement>? Files { get; init; }
    }

}
