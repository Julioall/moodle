using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Configuration;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure.Configuration;

namespace Moodle.Infrastructure;

internal sealed class MoodleGradebookGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient,
    IMemoryCache? memoryCache = null,
    IOptions<MoodleSnapshotOptions>? snapshotOptions = null,
    MoodleSnapshotMetrics? metrics = null,
    IMoodleFunctionCatalog? functionCatalog = null,
    IMoodleCourseGradeMaxGateway? courseGradeMaxGateway = null) : IMoodleGradebookGateway
{
    private const string MoodleFunction = "gradereport_user_get_grade_items";
    private readonly MoodleApiOptions _options = options.Value;
    private readonly MoodleSnapshotOptions _snapshotOptions =
        (snapshotOptions?.Value ?? new MoodleSnapshotOptions()).Normalize();

    public Task<CourseGradebook> GetStudentGradebookAsync(string courseId, string studentId, CancellationToken cancellationToken) =>
        GetStudentGradebookAsync(courseId, studentId, GradebookReadOptions.Default, cancellationToken);

    public async Task<CourseGradebook> GetStudentGradebookAsync(
        string courseId,
        string studentId,
        GradebookReadOptions readOptions,
        CancellationToken cancellationToken)
    {
        var courseIdNumber = ParseMoodleId(courseId, nameof(courseId));
        var studentIdNumber = ParseMoodleId(studentId, nameof(studentId));

        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var cacheKey = $"moodle-gradebook:{credentials.ConnectionId ?? credentials.Alias}:{courseIdNumber}:{studentIdNumber}";
        if (!readOptions.BypassCache && memoryCache?.TryGetValue(cacheKey, out CourseGradebook? cached) == true && cached is not null)
        {
            metrics?.RecordGradebookCacheHit("individual");
            return cached;
        }

        var payload = await ReadAsync(credentials, readOptions, new Dictionary<string, object?>
        {
            ["courseid"] = courseIdNumber.ToString(CultureInfo.InvariantCulture),
            ["userid"] = studentIdNumber.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken);

        var gradebook = ParseGradebook(payload.GetRawText(), courseId, studentId, studentIdNumber)
            with { ReadAt = DateTimeOffset.UtcNow };
        gradebook = await EnrichCourseGradeMaxAsync(gradebook, readOptions, cancellationToken);
        if (!readOptions.BypassCache) memoryCache?.Set(cacheKey, gradebook, TimeSpan.FromMinutes(_snapshotOptions.GradebookFreshMinutes));
        metrics?.RecordGradebookRead("individual");
        return gradebook;
    }

    public Task<CourseGradebookSnapshot> GetCourseGradebookAsync(string courseId, IReadOnlyCollection<string> studentIds,
        int? groupId, CancellationToken cancellationToken) =>
        GetCourseGradebookAsync(courseId, studentIds, groupId, GradebookReadOptions.Default, cancellationToken);

    public async Task<CourseGradebookSnapshot> GetCourseGradebookAsync(
        string courseId,
        IReadOnlyCollection<string> studentIds,
        int? groupId,
        GradebookReadOptions readOptions,
        CancellationToken cancellationToken)
    {
        if (readOptions.SingleAttempt && !readOptions.AllowCourseBulkRead)
            throw new NotSupportedException("This single-attempt read mode does not allow a course-level request.");
        var courseIdNumber = ParseMoodleId(courseId, nameof(courseId));
        var normalizedStudentIds = studentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedStudentIds.Length == 0)
        {
            return new CourseGradebookSnapshot(
                courseId,
                new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase),
                new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], []))
                .WithCanonicalProjection();
        }
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        if (!_snapshotOptions.BulkGradebookEnabled || normalizedStudentIds.Length > _snapshotOptions.MaxBulkGradebookStudents)
        {
            var warning = _snapshotOptions.BulkGradebookEnabled
                ? "bulk_student_count_exceeded"
                : "bulk_disabled";
            metrics?.RecordGradebookFallback(_snapshotOptions.BulkGradebookEnabled ? "student_limit" : "disabled");
            return await ReadIndividualGradebooksAsync(
                credentials,
                courseId,
                normalizedStudentIds,
                sourceMode: "individual_fallback",
                warning,
                readOptions,
                cancellationToken);
        }

        // Site-info capability discovery is cached by the catalog and keeps a
        // connection that does not expose the collective report endpoint on
        // the safe individual path. A missing/failed discovery is treated as
        // unavailable rather than optimistic bulk support.
        if (functionCatalog is not null)
        {
            bool bulkAvailable;
            try
            {
                var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
                bulkAvailable = profile.Functions.Any(function =>
                    function.IsAvailable &&
                    string.Equals(function.Name, MoodleFunction, StringComparison.OrdinalIgnoreCase));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                bulkAvailable = false;
            }

            if (!bulkAvailable)
            {
                metrics?.RecordGradebookFallback("capability_unavailable");
                return await ReadIndividualGradebooksAsync(
                    credentials,
                    courseId,
                    normalizedStudentIds,
                    sourceMode: "individual_fallback",
                    warning: "bulk_capability_unavailable",
                    readOptions,
                    cancellationToken);
            }
        }

        var cacheKey = $"moodle-gradebook-bulk:{credentials.ConnectionId ?? credentials.Alias}:{courseIdNumber}:{groupId ?? 0}";

        if (!readOptions.BypassCache && memoryCache?.TryGetValue(cacheKey, out CourseGradebookSnapshot? cached) == true && cached is not null)
        {
            metrics?.RecordGradebookCacheHit("bulk");
            return ApplyCoverage(cached, normalizedStudentIds);
        }

        var parameters = new Dictionary<string, object?>
        {
            ["courseid"] = courseIdNumber.ToString(CultureInfo.InvariantCulture),
            // userid=0 asks Moodle for all users visible to the caller.
            ["userid"] = "0",
            ["groupid"] = (groupId.GetValueOrDefault()).ToString(CultureInfo.InvariantCulture)
        };

        CourseGradebookSnapshot parsed;
        try
        {
            var payload = await ReadAsync(credentials, readOptions, parameters, cancellationToken);
            var rawPayload = payload.GetRawText();
            var payloadBytes = Encoding.UTF8.GetByteCount(rawPayload);
            if (payloadBytes > _snapshotOptions.MaxPayloadBytes)
            {
                metrics?.RecordGradebookFallback("payload_limit");
                throw new InvalidOperationException(
                    $"O payload bulk do gradebook excede o limite configurado de {_snapshotOptions.MaxPayloadBytes} bytes.");
            }
            parsed = ParseBulkGradebook(rawPayload, courseId, payloadBytes) with { ReadAt = DateTimeOffset.UtcNow };
            parsed = await EnrichCourseGradeMaxAsync(parsed, readOptions, cancellationToken);
            var cells = parsed.Gradebooks.Sum(item => item.Value.Items.Count);
            if (cells > _snapshotOptions.MaxBulkGradebookCells)
            {
                metrics?.RecordGradebookFallback("cell_limit");
                throw new InvalidOperationException(
                    $"O gradebook bulk ({cells} células) excede o limite configurado de {_snapshotOptions.MaxBulkGradebookCells}.");
            }
            metrics?.RecordGradebookRead("bulk");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            metrics?.RecordGradebookFallback("bulk_error");
            return await ReadIndividualGradebooksAsync(
                credentials,
                courseId,
                normalizedStudentIds,
                sourceMode: "individual_fallback",
                warning: "bulk_read_failed",
                readOptions,
                cancellationToken);
        }

        var covered = ApplyCoverage(parsed, normalizedStudentIds);
        if (covered.Coverage.MissingStudentIds.Count == 0)
        {
            if (!readOptions.BypassCache) memoryCache?.Set(cacheKey, parsed, TimeSpan.FromMinutes(_snapshotOptions.GradebookFreshMinutes));
            return covered;
        }

        // Keep the successful bulk rows and retry only the population that
        // Moodle omitted. This makes partial visibility explicit without
        // multiplying calls for already covered students.
        metrics?.RecordGradebookFallback("missing_students");
        var fallback = await ReadIndividualGradebooksAsync(
            credentials,
            courseId,
            covered.Coverage.MissingStudentIds,
            sourceMode: "mixed",
            warning: "bulk_missing_requested_users",
            readOptions,
            cancellationToken);
        var merged = Merge(covered, fallback, normalizedStudentIds);
        if (!readOptions.BypassCache) memoryCache?.Set(cacheKey, merged, TimeSpan.FromMinutes(_snapshotOptions.GradebookFreshMinutes));
        return merged;
    }

    private Task<JsonElement> ReadAsync(MoodleCredentials credentials, GradebookReadOptions readOptions,
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        readOptions.SingleAttempt ? restClient.CallReadOnceAsync(credentials, MoodleFunction, parameters, cancellationToken)
            : restClient.CallAsync(credentials, MoodleFunction, parameters, cancellationToken);

    private async Task<CourseGradebookSnapshot> ReadIndividualGradebooksAsync(
        MoodleCredentials credentials,
        string courseId,
        IReadOnlyCollection<string> studentIds,
        string sourceMode,
        string warning,
        GradebookReadOptions readOptions,
        CancellationToken cancellationToken)
    {
        var gradebooks = new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var errors = new List<string>();
        var warnings = new List<string> { warning };
        var normalizedIds = studentIds.ToArray();
        var workerCount = Math.Min(_snapshotOptions.IndividualGradebookConcurrency, normalizedIds.Length);
        var nextIndex = -1;
        var workers = Enumerable.Range(0, workerCount).Select(_ => ReadNextAsync()).ToArray();
        await Task.WhenAll(workers);
        var snapshot = new CourseGradebookSnapshot(
            courseId,
            gradebooks,
            new GradebookSnapshotCoverage(
                sourceMode,
                studentIds.Count,
                gradebooks.Count,
                missing.Count == 0,
                false,
                missing,
                warnings)
            {
                RequestedStudentIdsHash = CreateStudentIdsHash(studentIds),
                ErrorStudentIds = errors,
            })
            .WithCanonicalProjection();
        return await EnrichCourseGradeMaxAsync(snapshot, readOptions, cancellationToken);

        async Task ReadNextAsync()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= normalizedIds.Length) return;
                var studentId = normalizedIds[index];
                try
                {
                    var studentIdNumber = ParseMoodleId(studentId, nameof(studentId));
                    var cacheKey = $"moodle-gradebook:{credentials.ConnectionId ?? credentials.Alias}:{ParseMoodleId(courseId, nameof(courseId))}:{studentIdNumber}";
                    if (!readOptions.BypassCache && memoryCache?.TryGetValue(cacheKey, out CourseGradebook? cached) == true && cached is not null)
                    {
                        lock (gradebooks) gradebooks[studentId] = cached;
                        continue;
                    }

                    var payload = await ReadAsync(credentials, readOptions, new Dictionary<string, object?>
                    {
                        ["courseid"] = ParseMoodleId(courseId, nameof(courseId)).ToString(CultureInfo.InvariantCulture),
                        ["userid"] = studentIdNumber.ToString(CultureInfo.InvariantCulture)
                    }, cancellationToken);
                    var gradebook = ParseGradebook(payload.GetRawText(), courseId, studentId, studentIdNumber)
                        with { ReadAt = DateTimeOffset.UtcNow };
                    if (!readOptions.BypassCache) gradebook = await EnrichCourseGradeMaxAsync(gradebook, readOptions, cancellationToken);
                    if (!readOptions.BypassCache) memoryCache?.Set(cacheKey, gradebook, TimeSpan.FromMinutes(_snapshotOptions.GradebookFreshMinutes));
                    lock (gradebooks) gradebooks[studentId] = gradebook;
                    metrics?.RecordGradebookRead("individual_fallback");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    lock (missing)
                    {
                        missing.Add(studentId);
                        errors.Add(studentId);
                        warnings.Add($"student_read_failed:{exception.GetType().Name}");
                    }
                }
            }
        }
    }

    private async Task<CourseGradebook> EnrichCourseGradeMaxAsync(
        CourseGradebook gradebook,
        GradebookReadOptions readOptions,
        CancellationToken cancellationToken)
    {
        if (!readOptions.EnrichScale || courseGradeMaxGateway is null || gradebook.Items.Count == 0)
        {
            return gradebook;
        }

        var resolution = await courseGradeMaxGateway.ResolveAsync(
            gradebook.CourseId,
            gradebook.Items,
            readOptions.BypassCache,
            cancellationToken);
        return ApplyResolvedCourseMax(gradebook, resolution.MaxGrade) with
        {
            ScaleSource = resolution.Source,
            ScaleReadAt = resolution.Source == "gradebook" ? gradebook.ReadAt : DateTimeOffset.UtcNow
        };
    }

    private async Task<CourseGradebookSnapshot> EnrichCourseGradeMaxAsync(
        CourseGradebookSnapshot snapshot,
        GradebookReadOptions readOptions,
        CancellationToken cancellationToken)
    {
        if (!readOptions.EnrichScale || courseGradeMaxGateway is null || snapshot.Gradebooks.Count == 0)
        {
            return snapshot;
        }

        var allItems = snapshot.Gradebooks.Values
            .SelectMany(gradebook => gradebook.Items)
            .ToArray();
        var resolution = await courseGradeMaxGateway.ResolveAsync(
            snapshot.CourseId,
            allItems,
            readOptions.BypassCache,
            cancellationToken);
        var scaleAt = resolution.Source == "gradebook"
            ? snapshot.ReadAt ?? snapshot.Gradebooks.Values.Select(g => g.ReadAt).Max()
            : DateTimeOffset.UtcNow;
        var gradebooks = snapshot.Gradebooks.ToDictionary(
            item => item.Key,
            item => ApplyResolvedCourseMax(item.Value, resolution.MaxGrade) with
                { ScaleSource = resolution.Source, ScaleReadAt = scaleAt },
            StringComparer.OrdinalIgnoreCase);
        var warnings = snapshot.Coverage.Warnings ?? [];
        if (!string.IsNullOrWhiteSpace(resolution.Warning))
        {
            warnings = warnings
                .Append(resolution.Warning)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return (snapshot with
        {
            Gradebooks = gradebooks,
            ScaleSource = resolution.Source,
            ScaleReadAt = scaleAt,
            Coverage = snapshot.Coverage with { Warnings = warnings },
        }).WithCanonicalProjection();
    }

    private static CourseGradebook ApplyResolvedCourseMax(
        CourseGradebook gradebook,
        decimal? resolvedMax)
    {
        if (resolvedMax is not > 0m)
        {
            return gradebook;
        }

        var items = gradebook.Items
            .Select(item => string.Equals(item.ItemType, "course", StringComparison.OrdinalIgnoreCase) &&
                            item.GradeMax is not > 0m
                ? item with { GradeMax = resolvedMax }
                : item)
            .ToArray();
        return gradebook with { Items = items };
    }

    private static CourseGradebookSnapshot Merge(
        CourseGradebookSnapshot bulk,
        CourseGradebookSnapshot fallback,
        IReadOnlyCollection<string> requestedStudentIds)
    {
        var gradebooks = new Dictionary<string, CourseGradebook>(bulk.Gradebooks, StringComparer.OrdinalIgnoreCase);
        foreach (var item in fallback.Gradebooks)
        {
            gradebooks[item.Key] = item.Value;
        }

        var missing = requestedStudentIds
            .Where(id => !gradebooks.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var warnings = (bulk.Coverage.Warnings ?? [])
            .Concat(fallback.Coverage.Warnings ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var errors = (bulk.Coverage.ErrorStudentIds ?? [])
            .Concat(fallback.Coverage.ErrorStudentIds ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (bulk with
        {
            Gradebooks = gradebooks,
            Coverage = bulk.Coverage with
            {
                SourceMode = "mixed",
                RequestedStudentCount = requestedStudentIds.Count,
                ReturnedStudentCount = requestedStudentIds.Count - missing.Length,
                IsComplete = missing.Length == 0 && fallback.Coverage.IsComplete,
                MissingStudentIds = missing,
                Warnings = warnings,
                ErrorStudentIds = errors,
            }
        }).WithCanonicalProjection();
    }

    private static CourseGradebook ParseGradebook(string payload, string courseId, string studentId, long expectedStudentId)
    {
        var items = new List<GradebookItem>();

        if (string.IsNullOrWhiteSpace(payload))
        {
            return new CourseGradebook(courseId, studentId, items);
        }

        using var document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("usergrades", out var userGrades) || userGrades.ValueKind != JsonValueKind.Array)
        {
            return new CourseGradebook(courseId, studentId, items);
        }

        foreach (var userGrade in userGrades.EnumerateArray())
        {
            if (TryReadLongProperty(userGrade, "userid", "user_id", "id") is { } actualStudentId &&
                actualStudentId != expectedStudentId)
            {
                continue;
            }
            if (userGrade.TryGetProperty("gradeitems", out var gradeItems) && gradeItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in gradeItems.EnumerateArray())
                {
                    items.Add(ParseGradebookItem(item));
                }
            }
        }

        return new CourseGradebook(courseId, studentId, items);
    }

    private static CourseGradebookSnapshot ParseBulkGradebook(string payload, string courseId, long payloadBytes)
    {
        var gradebooks = new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new CourseGradebookSnapshot(courseId, gradebooks,
                new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], [])
                {
                    PayloadBytes = payloadBytes,
                }).WithCanonicalProjection();
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("usergrades", out var userGrades) ||
            userGrades.ValueKind != JsonValueKind.Array)
        {
            return new CourseGradebookSnapshot(courseId, gradebooks,
                new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], ["usergrades_missing"])
                {
                    PayloadBytes = payloadBytes,
                }).WithCanonicalProjection();
        }

        foreach (var userGrade in userGrades.EnumerateArray())
        {
            var studentId = ReadStringProperty(userGrade, "userid", "user_id", "id");
            if (string.IsNullOrWhiteSpace(studentId))
            {
                continue;
            }

            var items = new List<GradebookItem>();
            if (userGrade.TryGetProperty("gradeitems", out var gradeItems) && gradeItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in gradeItems.EnumerateArray())
                {
                    items.Add(ParseGradebookItem(item));
                }
            }

            gradebooks[studentId] = new CourseGradebook(courseId, studentId, items);
        }

        return new CourseGradebookSnapshot(courseId, gradebooks,
            new GradebookSnapshotCoverage("bulk", 0, gradebooks.Count, true, false, [], [])
            {
                PayloadBytes = payloadBytes,
            }).WithCanonicalProjection();
    }

    private static CourseGradebookSnapshot ApplyCoverage(
        CourseGradebookSnapshot snapshot,
        IReadOnlyCollection<string> requestedStudentIds)
    {
        var requested = requestedStudentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopedGradebooks = snapshot.Gradebooks
            .Where(item => requestedSet.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var missing = requested
            .Where(id => !scopedGradebooks.ContainsKey(id))
            .ToArray();
        var returned = requested.Length - missing.Length;
        var warnings = (snapshot.Coverage.Warnings ?? []).ToList();
        if (missing.Length > 0 && snapshot.Coverage.SourceMode == "bulk")
        {
            warnings.Add("bulk_missing_requested_users");
        }

        return (snapshot with
        {
            Gradebooks = scopedGradebooks,
            Coverage = snapshot.Coverage with
            {
                RequestedStudentCount = requested.Length,
                ReturnedStudentCount = returned,
                IsComplete = missing.Length == 0,
                MissingStudentIds = missing,
                Warnings = warnings,
                RequestedStudentIdsHash = CreateStudentIdsHash(requested),
            }
        }).WithCanonicalProjection();
    }

    private static GradebookItem ParseGradebookItem(JsonElement item) => new(
        Id: ReadStringProperty(item, "id") ?? string.Empty,
        ItemName: ReadStringProperty(item, "itemname") ?? string.Empty,
        ItemType: ReadStringProperty(item, "itemtype") ?? string.Empty,
        ItemModule: ReadStringProperty(item, "itemmodule") ?? string.Empty,
        CategoryId: ReadStringProperty(item, "categoryid"),
        GradeRaw: ReadDecimalProperty(item, "graderaw"),
        GradeFormatted: ReadStringProperty(item, "gradeformatted"),
        GradeMin: ReadDecimalProperty(item, "grademin", "min", "mingrade"),
        GradeMax: ReadDecimalProperty(item, "grademax", "max", "maxgrade", "grade_max"),
        PercentageFormatted: ReadDecimalProperty(item, "percentageformatted", "percentage", "percent"),
        Feedback: ReadStringProperty(item, "feedback"),
        FeedbackFormat: ReadStringProperty(item, "feedbackformat"),
        GradedDateSubmitted: ReadLongProperty(item, "gradeddatesubmitted"),
        // Moodle names this field "gradedategraded" (one d after grade).
        GradedDateGraded: ReadLongProperty(item, "gradedategraded", "gradeddategraded"),
        GraderId: ReadStringProperty(item, "grader"),
        ItemInstance: ReadStringProperty(item, "iteminstance"),
        CourseModuleId: ReadStringProperty(item, "cmid"),
        ScaleId: ReadLongProperty(item, "scaleid"),
        GradeType: ReadLongProperty(item, "gradetype"),
        FeedbackFieldPresent: item.ValueKind == JsonValueKind.Object && item.TryGetProperty("feedback", out _));

    private static string? ReadStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }
        return null;
    }

    private static decimal? ReadDecimalProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d))
                return d;
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim().TrimEnd('%').Trim();
                var ptBrFirst = text?.Contains(',', StringComparison.Ordinal) == true &&
                    text.Contains('.', StringComparison.Ordinal) == false;
                if (ptBrFirst
                    ? decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out var ds) ||
                      decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out ds)
                    : decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out ds) ||
                      decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out ds))
                {
                    return ds;
                }
            }
        }
        return null;
    }

    private static long? ReadLongProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var d))
                return d;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
                return ds;
        }
        return null;
    }

    private static long ParseMoodleId(string value, string parameterName)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            return id;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico do Moodle.", parameterName);
    }

    private static long? TryReadLongProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text))
            {
                return text;
            }
        }

        return null;
    }

    private static string CreateStudentIdsHash(IEnumerable<string> studentIds)
    {
        var canonical = string.Join('\n', studentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

}
