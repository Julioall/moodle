using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure.Configuration;

namespace Moodle.Infrastructure;

internal sealed class MoodleAssignmentGradeReadGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient,
    IOptions<MoodleSnapshotOptions>? snapshotOptions = null) : IMoodleAssignmentGradeReadGateway
{
    private const string MoodleFunction = "mod_assign_get_grades";
    private readonly MoodleApiOptions _options = options.Value;
    private readonly MoodleSnapshotOptions _snapshotOptions =
        (snapshotOptions?.Value ?? new MoodleSnapshotOptions()).Normalize();

    public async Task<IReadOnlyList<AssignmentGradesBatch>> GetExistingGradesBatchAsync(
        string userExternalId,
        IReadOnlyCollection<string> assignmentIds,
        IReadOnlyCollection<string> studentIds,
        CancellationToken cancellationToken)
    {
        var normalizedAssignmentIds = assignmentIds
            .Select(id => TryParseMoodleId(id, out var value) ? value.ToString(CultureInfo.InvariantCulture) : null)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedAssignmentIds.Length == 0)
        {
            return [];
        }

        if (_options.UseStubData || studentIds.Count == 0)
        {
            return normalizedAssignmentIds
                .Select(id => new AssignmentGradesBatch(
                    id,
                    new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase)))
                .ToArray();
        }

        if (string.IsNullOrWhiteSpace(userExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(userExternalId));
        }

        var requestedStudentIds = studentIds
            .Select(studentId => TryParseMoodleId(studentId, out var value) ? value : 0L)
            .Where(id => id > 0)
            .ToHashSet();
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var result = new List<AssignmentGradesBatch>(normalizedAssignmentIds.Length);

        foreach (var chunk in normalizedAssignmentIds.Chunk(_snapshotOptions.AssignmentGradeBatchSize))
        {
            try
            {
                var parsed = await ReadGradesChunkAsync(
                    credentials,
                    chunk,
                    requestedStudentIds,
                    cancellationToken);
                var missing = new List<string>();
                foreach (var assignmentId in chunk)
                {
                    if (parsed.TryGetValue(assignmentId, out var grades))
                    {
                        result.Add(new AssignmentGradesBatch(assignmentId, grades));
                    }
                    else
                    {
                        missing.Add(assignmentId);
                    }
                }

                // A Moodle installation may omit one inaccessible assignment
                // from an otherwise valid bulk response. Isolate only those
                // IDs so the valid part of the set remains usable.
                if (missing.Count > 0)
                {
                    result.AddRange(await ReadSingleGradesConcurrentlyAsync(
                        credentials,
                        missing,
                        requestedStudentIds,
                        cancellationToken));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // A rejected multi-assignment request must not discard the
                // assignments that could still be read safely. Retry only
                // this chunk individually with bounded concurrency; each
                // assignment keeps its own error contract if the retry fails.
                result.AddRange(await ReadSingleGradesConcurrentlyAsync(
                    credentials,
                    chunk,
                    requestedStudentIds,
                    cancellationToken));
            }
        }

        return result;
    }

    public async Task<AssignmentExistingGrade?> GetExistingGradeAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(userExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(userExternalId));
        }

        var grades = await GetExistingGradesAsync(
            userExternalId,
            assignmentId,
            [studentId],
            cancellationToken);
        return grades.GetValueOrDefault(studentId);
    }

    public async Task<IReadOnlyDictionary<string, AssignmentExistingGrade>> GetExistingGradesAsync(
        string userExternalId,
        string assignmentId,
        IReadOnlyCollection<string> studentIds,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData || studentIds.Count == 0)
        {
            return new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(userExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(userExternalId));
        }

        var assignmentIdNumber = ParseMoodleId(assignmentId, nameof(assignmentId));
        var requestedStudentIds = studentIds
            .Select(studentId => ParseMoodleId(studentId, nameof(studentIds)))
            .ToHashSet();
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var parsed = await ReadGradesChunkAsync(
            credentials,
            [assignmentIdNumber.ToString(CultureInfo.InvariantCulture)],
            requestedStudentIds,
            cancellationToken);
        return parsed.GetValueOrDefault(
                   assignmentIdNumber.ToString(CultureInfo.InvariantCulture))
               ?? new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>> ReadGradesChunkAsync(
        MoodleCredentials credentials,
        IReadOnlyCollection<string> assignmentIds,
        IReadOnlySet<long> requestedStudentIds,
        CancellationToken cancellationToken)
    {
        var parameters = assignmentIds
            .Select((assignmentId, index) => new KeyValuePair<string, object?>(
                $"assignmentids[{index}]",
                assignmentId))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var payload = await restClient.CallAsync(
            credentials,
            MoodleFunction,
            parameters,
            cancellationToken);
        return ParseGrades(payload.GetRawText(), requestedStudentIds, assignmentIds);
    }

    private async Task<IReadOnlyList<AssignmentGradesBatch>> ReadSingleGradesConcurrentlyAsync(
        MoodleCredentials credentials,
        IReadOnlyCollection<string> assignmentIds,
        IReadOnlySet<long> requestedStudentIds,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(
            _snapshotOptions.FeedbackReadConcurrency,
            _snapshotOptions.FeedbackReadConcurrency);
        var batches = await Task.WhenAll(assignmentIds.Select(async assignmentId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                // The batch-level fallback converts failures into per-assignment results,
                // so its caller cannot retry a transient network error after this point.
                var parsed = await MoodleReadRetry.ExecuteAsync(
                    token => ReadGradesChunkAsync(
                        credentials,
                        [assignmentId],
                        requestedStudentIds,
                        token),
                    onRetry: null,
                    cancellationToken);
                return parsed.TryGetValue(assignmentId, out var grades)
                    ? new AssignmentGradesBatch(assignmentId, grades)
                    : new AssignmentGradesBatch(
                        assignmentId,
                        new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase),
                        "assignment_not_returned",
                        "A tarefa não foi retornada pelo Moodle no lote de notas.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failure = MoodleErrorContract.Describe(exception);
                return new AssignmentGradesBatch(
                    assignmentId,
                    new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase),
                    failure.ErrorCode,
                    failure.Message);
            }
            finally
            {
                gate.Release();
            }
        }));
        return batches;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>> ParseGrades(
        string payload,
        IReadOnlySet<long> requestedStudentIds,
        IReadOnlyCollection<string> requestedAssignmentIds)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, AssignmentExistingGrade>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return result;
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("assignments", out var assignments) ||
            assignments.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var assignment in assignments.EnumerateArray())
        {
            if (!TryReadLongProperty(assignment, "assignmentid", out var assignmentId))
            {
                continue;
            }

            var assignmentGrades = new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase);
            if (assignment.TryGetProperty("grades", out var grades) && grades.ValueKind == JsonValueKind.Array)
            {
                foreach (var grade in grades.EnumerateArray())
                {
                    if (!TryReadLongProperty(grade, "userid", out var studentId) ||
                        !requestedStudentIds.Contains(studentId))
                    {
                        continue;
                    }

                    var parsedGrade = ReadDecimalProperty(grade, "grade");
                    var studentIdText = studentId.ToString(CultureInfo.InvariantCulture);
                    assignmentGrades[studentIdText] = new AssignmentExistingGrade(
                        assignmentId.ToString(CultureInfo.InvariantCulture),
                        studentIdText,
                        parsedGrade,
                        HasGrade: parsedGrade is >= 0,
                        Feedback: ReadTextProperty(grade, "feedback")
                            ?? ReadTextProperty(grade, "feedbacktext")
                            ?? ReadTextProperty(grade, "feedbackcomments"),
                        GradeMax: ReadDecimalProperty(grade, "grademax"),
                        GraderId: ReadLongProperty(grade, "grader"),
                        TimeModified: ReadLongProperty(grade, "timemodified"),
                        FeedbackObserved: grade.TryGetProperty("feedback", out _) ||
                                          grade.TryGetProperty("feedbacktext", out _) ||
                                          grade.TryGetProperty("feedbackcomments", out _));
                }
            }

            // An assignment with no grades is a successful empty set. This
            // differs from an assignment omitted from the response.
            result[assignmentId.ToString(CultureInfo.InvariantCulture)] = assignmentGrades;
        }

        // Moodle emits warning code 3 ("No grades found") instead of an
        // assignment entry when an activity has no grade rows yet. That is a
        // valid, complete empty grade set—not an omitted/inaccessible
        // assignment. Preserve it as an empty batch so snapshot coverage can
        // still be complete and pending submissions remain eligible for review.
        if (document.RootElement.TryGetProperty("warnings", out var warnings) &&
            warnings.ValueKind == JsonValueKind.Array)
        {
            var requested = requestedAssignmentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var warning in warnings.EnumerateArray())
            {
                if (!TryReadLongProperty(warning, "itemid", out var itemId) ||
                    !requested.Contains(itemId.ToString(CultureInfo.InvariantCulture)) ||
                    !string.Equals(ReadTextProperty(warning, "warningcode"), "3", StringComparison.Ordinal))
                {
                    continue;
                }

                result.TryAdd(
                    itemId.ToString(CultureInfo.InvariantCulture),
                    new Dictionary<string, AssignmentExistingGrade>(StringComparer.OrdinalIgnoreCase));
            }
        }

        return result;
    }

    private static bool TryReadLongProperty(JsonElement element, string propertyName, out long result)
    {
        result = 0;
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out result),
            JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result),
            _ => false
        };
    }

    private static long? ReadLongProperty(JsonElement element, string propertyName) =>
        TryReadLongProperty(element, propertyName, out var value) ? value : null;

    private static decimal? ReadDecimalProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var grade) => grade,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var grade) => grade,
            _ => null
        };
    }

    private static string? ReadTextProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var childName in new[] { "text", "content", "message" })
            {
                if (value.TryGetProperty(childName, out var child) && child.ValueKind == JsonValueKind.String)
                {
                    return child.GetString();
                }
            }
        }

        return null;
    }

    private static long ParseMoodleId(string value, string parameterName)
    {
        if (TryParseMoodleId(value, out var id))
        {
            return id;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico do Moodle.", parameterName);
    }

    private static bool TryParseMoodleId(string value, out long id) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
}
