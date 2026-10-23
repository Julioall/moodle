using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moodle.Application.Abstractions;

public record GradebookItem(
    string Id,
    string ItemName,
    string ItemType,
    string ItemModule,
    string? CategoryId,
    decimal? GradeRaw,
    string? GradeFormatted,
    decimal? GradeMin,
    decimal? GradeMax,
    decimal? PercentageFormatted,
    string? Feedback,
    string? FeedbackFormat,
    long? GradedDateSubmitted,
    long? GradedDateGraded,
    string? GraderId,
    string? ItemInstance = null,
    string? CourseModuleId = null,
    long? ScaleId = null,
    long? GradeType = null,
    bool FeedbackFieldPresent = false);

public record CourseGradebook(
    string CourseId,
    string StudentId,
    IReadOnlyCollection<GradebookItem> Items)
{
    public DateTimeOffset? ReadAt { get; init; }
    public DateTimeOffset? ScaleReadAt { get; init; }
    public string? ScaleSource { get; init; }
}

/// <summary>
/// Course-level definition of a grade item. Definitions are de-duplicated by
/// <see cref="GradeItemId"/> in the canonical snapshot projection.
/// </summary>
public sealed record GradebookItemDefinition(
    string GradeItemId,
    string ItemName,
    string ItemType,
    string ItemModule,
    string? ItemInstance,
    string? CourseModuleId,
    string? CategoryId,
    decimal? GradeMin,
    decimal? GradeMax);

/// <summary>
/// Student-specific values for one grade item. Keeping this separate from the
/// item definition avoids repeating names, categories and limits for every
/// student in the persisted course head.
/// </summary>
public sealed record StudentGradebookEntry(
    string StudentId,
    string GradeItemId,
    decimal? GradeRaw,
    string? GradeFormatted,
    decimal? Percentage,
    string? Feedback,
    string? FeedbackFormat,
    long? GradedDateSubmitted,
    long? GradedDateGraded,
    string? GraderId);

/// <summary>
/// Course-level gradebook read. The dictionary deliberately keeps an entry for
/// users returned by Moodle even when they have no grade items, so callers can
/// distinguish an empty gradebook from a missing user and only fall back for
/// genuinely uncovered students.
/// </summary>
[JsonConverter(typeof(CourseGradebookSnapshotJsonConverter))]
public sealed record CourseGradebookSnapshot(
    string CourseId,
    [property: JsonIgnore] IReadOnlyDictionary<string, CourseGradebook> Gradebooks,
    GradebookSnapshotCoverage Coverage)
{
    public DateTimeOffset? ReadAt { get; init; }
    public DateTimeOffset? ScaleReadAt { get; init; }
    public string? ScaleSource { get; init; }
    /// <summary>
    /// Canonical, de-duplicated item definitions. These additive fields are
    /// populated by gateways before publication; old snapshots may omit them
    /// and can be upgraded lazily with <see cref="WithCanonicalProjection"/>.
    /// </summary>
    public IReadOnlyCollection<GradebookItemDefinition> Items { get; init; } = [];

    /// <summary>
    /// Canonical student/item values indexed by (StudentId, GradeItemId).
    /// </summary>
    public IReadOnlyCollection<StudentGradebookEntry> StudentGrades { get; init; } = [];

    public CourseGradebook? ForStudent(string studentId) =>
        Gradebooks?.TryGetValue(studentId, out var gradebook) == true ? gradebook : null;

    public bool TryGetForStudent(string studentId, out CourseGradebook gradebook)
    {
        gradebook = null!;
        if (Gradebooks?.TryGetValue(studentId, out var candidate) == true)
        {
            gradebook = candidate;
            return true;
        }

        return false;
    }

    public string GetStudentCoverageState(string studentId)
    {
        if (Gradebooks?.TryGetValue(studentId, out var returned) == true)
        {
            return returned.Items.Count == 0
                ? GradebookCoverageStates.Empty
                : GradebookCoverageStates.Covered;
        }

        if (Coverage.ErrorStudentIds?.Contains(studentId, StringComparer.OrdinalIgnoreCase) == true)
        {
            return GradebookCoverageStates.Error;
        }

        if (Coverage.MissingStudentIds?.Contains(studentId, StringComparer.OrdinalIgnoreCase) == true)
        {
            return GradebookCoverageStates.NotReturned;
        }

        return GradebookCoverageStates.NotRequested;
    }

    /// <summary>
    /// Builds the normalized projection from the compatibility dictionary.
    /// The operation is deterministic and idempotent, making it safe to call
    /// both for fresh gateway results and for snapshots created before the
    /// canonical fields were introduced.
    /// </summary>
    public CourseGradebookSnapshot WithCanonicalProjection()
    {
        var definitions = new Dictionary<string, GradebookItemDefinition>(StringComparer.OrdinalIgnoreCase);
        var grades = new Dictionary<(string StudentId, string GradeItemId), StudentGradebookEntry>();

        foreach (var (studentId, gradebook) in Gradebooks ?? new Dictionary<string, CourseGradebook>())
        {
            foreach (var item in gradebook.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    continue;
                }

                var itemId = item.Id.Trim();
                definitions[itemId] = new GradebookItemDefinition(
                    itemId,
                    item.ItemName,
                    item.ItemType,
                    item.ItemModule,
                    item.ItemInstance,
                    item.CourseModuleId,
                    item.CategoryId,
                    item.GradeMin,
                    item.GradeMax);

                grades[(studentId, itemId)] = new StudentGradebookEntry(
                    studentId,
                    itemId,
                    item.GradeRaw,
                    item.GradeFormatted,
                    item.PercentageFormatted,
                    item.Feedback,
                    item.FeedbackFormat,
                    item.GradedDateSubmitted,
                    item.GradedDateGraded,
                    item.GraderId);
            }
        }

        return this with
        {
            Items = definitions.Values
                .OrderBy(item => item.GradeItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StudentGrades = grades.Values
                .OrderBy(item => item.StudentId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.GradeItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Coverage = Coverage with
            {
                ReturnedStudentIds = (Gradebooks?.Keys ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            },
        };
    }
}

public sealed record GradebookSnapshotCoverage(
    string SourceMode,
    int RequestedStudentCount,
    int ReturnedStudentCount,
    bool IsComplete,
    bool Truncated,
    IReadOnlyCollection<string> MissingStudentIds,
    IReadOnlyCollection<string> Warnings)
{
    // Diagnostic-only identity for reconciling a request without persisting
    // the student list in logs or metrics. The hash is populated by the
    // gateway after IDs are normalized.
    public string? RequestedStudentIdsHash { get; init; }
    /// <summary>
    /// IDs returned by Moodle, including users with an empty grade item list.
    /// This keeps the compatibility projection lossless when the canonical
    /// payload is deserialized without persisting the per-user dictionary.
    /// </summary>
    public IReadOnlyCollection<string> ReturnedStudentIds { get; init; } = [];
    /// <summary>
    /// Requested users whose individual fallback failed. This is separate from
    /// users simply omitted by a bulk response, so reports never call either
    /// condition “sem nota”.
    /// </summary>
    public IReadOnlyCollection<string> ErrorStudentIds { get; init; } = [];
    public int WarningCount => Warnings?.Count ?? 0;
    public long PayloadBytes { get; init; }
}

public static class GradebookCoverageStates
{
    public const string Covered = "covered";
    public const string Empty = "empty";
    public const string ItemAbsent = "item_absent";
    public const string NoGrade = "no_grade";
    public const string NotReturned = "not_returned";
    public const string Error = "error";
    public const string NotRequested = "not_requested";
}

/// <summary>
/// Persists the normalized course gradebook (items + student/item values) and
/// reconstructs the legacy per-student dictionary on read. A legacy payload
/// containing <c>gradebooks</c> is still accepted so existing heads remain
/// readable without a destructive migration.
/// </summary>
public sealed class CourseGradebookSnapshotJsonConverter : JsonConverter<CourseGradebookSnapshot>
{
    public override CourseGradebookSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var courseId = ReadString(root, "courseId", "CourseId") ?? string.Empty;

        if (TryGetProperty(root, out var legacyGradebooks, "gradebooks", "Gradebooks") &&
            legacyGradebooks.ValueKind == JsonValueKind.Object)
        {
            var legacy = JsonSerializer.Deserialize<Dictionary<string, CourseGradebook>>(
                legacyGradebooks.GetRawText(), options)
                ?? new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase);
            var legacyCoverage = ReadCoverage(root, options);
            return new CourseGradebookSnapshot(courseId, legacy, legacyCoverage).WithCanonicalProjection();
        }

        var items = ReadArray<GradebookItemDefinition>(root, options, "items", "Items");
        var grades = ReadArray<StudentGradebookEntry>(root, options, "studentGrades", "StudentGrades");
        var coverage = ReadCoverage(root, options);
        var definitions = items.ToDictionary(item => item.GradeItemId, StringComparer.OrdinalIgnoreCase);
        var byStudent = new Dictionary<string, List<GradebookItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var returnedId in coverage.ReturnedStudentIds ?? [])
        {
            if (!string.IsNullOrWhiteSpace(returnedId))
            {
                byStudent[returnedId] = [];
            }
        }

        foreach (var grade in grades)
        {
            if (string.IsNullOrWhiteSpace(grade.StudentId) || string.IsNullOrWhiteSpace(grade.GradeItemId))
            {
                continue;
            }

            if (!byStudent.TryGetValue(grade.StudentId, out var studentItems))
            {
                studentItems = [];
                byStudent[grade.StudentId] = studentItems;
            }

            definitions.TryGetValue(grade.GradeItemId, out var definition);
            studentItems.Add(new GradebookItem(
                grade.GradeItemId,
                definition?.ItemName ?? string.Empty,
                definition?.ItemType ?? string.Empty,
                definition?.ItemModule ?? string.Empty,
                definition?.CategoryId,
                grade.GradeRaw,
                grade.GradeFormatted,
                definition?.GradeMin,
                definition?.GradeMax,
                grade.Percentage,
                grade.Feedback,
                grade.FeedbackFormat,
                grade.GradedDateSubmitted,
                grade.GradedDateGraded,
                grade.GraderId,
                definition?.ItemInstance,
                definition?.CourseModuleId));
        }

        var gradebooks = byStudent.ToDictionary(
            item => item.Key,
            item => new CourseGradebook(courseId, item.Key, item.Value),
            StringComparer.OrdinalIgnoreCase);
        return new CourseGradebookSnapshot(courseId, gradebooks, coverage)
        {
            Items = items,
            StudentGrades = grades,
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        CourseGradebookSnapshot value,
        JsonSerializerOptions options)
    {
        var normalized = value.WithCanonicalProjection();
        writer.WriteStartObject();
        writer.WriteString("courseId", normalized.CourseId);
        writer.WritePropertyName("items");
        JsonSerializer.Serialize(writer, normalized.Items, options);
        writer.WritePropertyName("studentGrades");
        JsonSerializer.Serialize(writer, normalized.StudentGrades, options);
        writer.WritePropertyName("coverage");
        JsonSerializer.Serialize(writer, normalized.Coverage, options);
        writer.WriteEndObject();
    }

    private static GradebookSnapshotCoverage ReadCoverage(JsonElement root, JsonSerializerOptions options)
    {
        if (TryGetProperty(root, out var coverageElement, "coverage", "Coverage") &&
            coverageElement.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<GradebookSnapshotCoverage>(coverageElement.GetRawText(), options)
                ?? new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], []);
        }

        return new GradebookSnapshotCoverage("bulk", 0, 0, true, false, [], []);
    }

    private static IReadOnlyList<T> ReadArray<T>(JsonElement root, JsonSerializerOptions options, params string[] names)
    {
        if (!TryGetProperty(root, out var element, names) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<T>>(element.GetRawText(), options) ?? [];
    }

    private static string? ReadString(JsonElement root, params string[] names) =>
        TryGetProperty(root, out var element, names) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool TryGetProperty(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }
}

public interface IMoodleGradebookGateway
{
    Task<CourseGradebook> GetStudentGradebookAsync(string courseId, string studentId,
        GradebookReadOptions readOptions, CancellationToken cancellationToken) =>
        readOptions == GradebookReadOptions.Default ? GetStudentGradebookAsync(courseId, studentId, cancellationToken)
            : throw new NotSupportedException("This gateway does not support explicit live reads.");

    Task<CourseGradebookSnapshot> GetCourseGradebookAsync(string courseId, IReadOnlyCollection<string> studentIds,
        int? groupId, GradebookReadOptions readOptions, CancellationToken cancellationToken) =>
        readOptions == GradebookReadOptions.Default
            ? GetCourseGradebookAsync(courseId, studentIds, groupId, cancellationToken)
            : ReadCourseGradebookWithOptionsAsync(this, courseId, studentIds, readOptions, cancellationToken);

    Task<CourseGradebook> GetStudentGradebookAsync(
        string courseId,
        string studentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a course gradebook in one bulk request when supported by the
    /// connector. The default implementation preserves compatibility with
    /// existing gateways by using bounded individual reads.
    /// </summary>
    async Task<CourseGradebookSnapshot> GetCourseGradebookAsync(
        string courseId,
        IReadOnlyCollection<string> studentIds,
        int? groupId,
        CancellationToken cancellationToken)
    {
        var normalizedStudentIds = studentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var gradebooks = new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var errors = new List<string>();
        var warnings = new List<string>();
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = normalizedStudentIds.Select(async studentId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var gradebook = await GetStudentGradebookAsync(courseId, studentId, cancellationToken);
                lock (gradebooks)
                {
                    gradebooks[studentId] = gradebook;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (missing)
                {
                    missing.Add(studentId);
                    errors.Add(studentId);
                    warnings.Add($"student_read_failed:{exception.GetType().Name}");
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        return new CourseGradebookSnapshot(
            courseId,
            gradebooks,
            new GradebookSnapshotCoverage(
                SourceMode: "individual_fallback",
                RequestedStudentCount: normalizedStudentIds.Length,
                ReturnedStudentCount: gradebooks.Count,
                IsComplete: missing.Count == 0,
                Truncated: false,
                MissingStudentIds: missing,
                Warnings: warnings)
            {
                ErrorStudentIds = errors,
            })
            .WithCanonicalProjection();
    }

    private static async Task<CourseGradebookSnapshot> ReadCourseGradebookWithOptionsAsync(
        IMoodleGradebookGateway gateway,
        string courseId,
        IReadOnlyCollection<string> studentIds,
        GradebookReadOptions readOptions,
        CancellationToken cancellationToken)
    {
        var normalizedStudentIds = studentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var gradebooks = new Dictionary<string, CourseGradebook>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var errors = new List<string>();
        var warnings = new List<string>();
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = normalizedStudentIds.Select(async studentId =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var gradebook = await gateway.GetStudentGradebookAsync(
                    courseId,
                    studentId,
                    readOptions,
                    cancellationToken);
                lock (gradebooks)
                {
                    gradebooks[studentId] = gradebook;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lock (missing)
                {
                    missing.Add(studentId);
                    errors.Add(studentId);
                    warnings.Add($"student_read_failed:{exception.GetType().Name}");
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        return new CourseGradebookSnapshot(
            courseId,
            gradebooks,
            new GradebookSnapshotCoverage(
                SourceMode: "individual_fallback",
                RequestedStudentCount: normalizedStudentIds.Length,
                ReturnedStudentCount: gradebooks.Count,
                IsComplete: missing.Count == 0,
                Truncated: false,
                MissingStudentIds: missing,
                Warnings: warnings)
            {
                ErrorStudentIds = errors,
            })
        {
            ReadAt = DateTimeOffset.UtcNow,
        }.WithCanonicalProjection();
    }
}
