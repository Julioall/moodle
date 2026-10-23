using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;

namespace Moodle.Infrastructure;

/// <summary>
/// Supplies a conservative course-scale fallback for Moodle installations
/// that omit <c>grademax</c> from the user grade report. The fallback is based
/// only on known evaluative module settings and never guesses from a student's
/// current score.
/// </summary>
internal sealed class MoodleCourseGradeMaxGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient,
    IMemoryCache memoryCache) : IMoodleCourseGradeMaxGateway
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private readonly MoodleApiOptions _options = options.Value;

    public Task<CourseGradeMaxResolution> ResolveAsync(string courseId, IReadOnlyCollection<GradebookItem> items,
        CancellationToken cancellationToken) => ResolveAsync(courseId, items, false, cancellationToken);

    public async Task<CourseGradeMaxResolution> ResolveAsync(
        string courseId,
        IReadOnlyCollection<GradebookItem> items,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        var normalizedCourseId = ParseMoodleId(courseId, nameof(courseId));
        var allItems = items ?? [];

        var explicitCourseMax = allItems
            .Where(IsCourseItem)
            .Select(item => Positive(item.GradeMax))
            .FirstOrDefault(value => value.HasValue);
        if (explicitCourseMax.HasValue)
        {
            return new CourseGradeMaxResolution(explicitCourseMax, "gradebook", null);
        }

        if (_options.UseStubData)
        {
            return Empty("grade_max_fallback_stub");
        }

        // The same activity appears once per student in a bulk response.
        // De-duplicate before summing maxima so the course scale is not
        // multiplied by the population size.
        var activities = allItems
            .Where(IsActivityItem)
            .GroupBy(ActivityIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (activities.Length == 0)
        {
            return Empty("grade_max_fallback_no_activities");
        }

        var pending = new List<GradebookItem>();
        var resolved = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in activities)
        {
            var module = ModuleName(item);
            if (module is "scorm")
            {
                // SCORM is a content package in the observed SENAI setup and
                // is dimmed in the course aggregation. It must not silently
                // increase the course total.
                continue;
            }

            if (!IsSupportedEvaluativeModule(module))
            {
                return Empty("grade_max_fallback_unknown_module");
            }

            if (Positive(item.GradeMax) is { } explicitMax)
            {
                resolved[ActivityIdentity(item)] = explicitMax;
            }
            else
            {
                pending.Add(item);
            }
        }

        if (pending.Count > 0)
        {
            var settings = await GetModuleMaximaAsync(normalizedCourseId, pending, bypassCache, cancellationToken);
            if (settings is null)
            {
                return Empty("grade_max_fallback_module_read_failed");
            }

            foreach (var item in pending)
            {
                var key = ActivityIdentity(item);
                if (!TryResolveModuleMax(settings, item, out var max))
                {
                    return Empty("grade_max_fallback_module_max_missing");
                }

                resolved[key] = max;
            }
        }

        if (resolved.Count == 0 || resolved.Values.Any(value => value <= 0m))
        {
            return Empty("grade_max_fallback_no_evaluative_items");
        }

        var candidate = resolved.Values.Sum();
        if (candidate <= 0m)
        {
            return Empty("grade_max_fallback_invalid_sum");
        }

        // If Moodle returned a course raw score, it must fit inside the
        // proposed scale. A score above the candidate indicates weighting or
        // an omitted activity and makes the inference unsafe.
        var courseRawGrades = allItems
            .Where(IsCourseItem)
            .Select(item => item.GradeRaw)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        if (courseRawGrades.Any(value => value > candidate + 0.0001m))
        {
            return Empty("grade_max_fallback_score_exceeds_sum");
        }

        // Require at least one observed student to reach the proposed scale
        // when Moodle supplied course totals. This prevents an incomplete
        // snapshot (for example, one missing quiz) from publishing a smaller
        // maximum simply because all currently visible grades fit inside it.
        if (courseRawGrades.Length > 0 &&
            !courseRawGrades.Any(value => Math.Abs(value - candidate) <= 0.0001m))
        {
            return Empty("grade_max_fallback_scale_not_observed");
        }

        var result = new CourseGradeMaxResolution(
            Math.Round(candidate, 4, MidpointRounding.AwayFromZero),
            "activity_sum",
            "grade_max_fallback_activity_sum");
        return result;
    }

    private async Task<IReadOnlyDictionary<string, decimal>?> GetModuleMaximaAsync(
        long courseId,
        IReadOnlyCollection<GradebookItem> items,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        MoodleCredentials credentials;
        try
        {
            credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        var modules = items
            .Select(ModuleName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (modules.Any(module => module is not "quiz" and not "assign" and not "assignment"))
        {
            return null;
        }

        var cacheKey = BuildModuleCacheKey(credentials, courseId, modules, items);
        if (!bypassCache && memoryCache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, decimal>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (modules.Any(module => module is "quiz"))
            {
                var payload = await restClient.CallAsync(
                    credentials,
                    "mod_quiz_get_quizzes_by_courses",
                    new Dictionary<string, object?>
                    {
                        ["courseids[0]"] = courseId.ToString(CultureInfo.InvariantCulture),
                    },
                    cancellationToken);
                ParseQuizMaxima(payload, result);
            }

            if (modules.Any(module => module is "assign" or "assignment"))
            {
                var payload = await restClient.CallAsync(
                    credentials,
                    "mod_assign_get_assignments",
                    new Dictionary<string, object?>
                    {
                        ["courseids[0]"] = courseId.ToString(CultureInfo.InvariantCulture),
                    },
                    cancellationToken);
                ParseAssignmentMaxima(payload, result);
            }

            if (!bypassCache) memoryCache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A missing module capability should not make the gradebook read
            // fail. The report remains honest by leaving the maximum null.
            return null;
        }
    }

    private static bool TryResolveModuleMax(
        IReadOnlyDictionary<string, decimal> settings,
        GradebookItem item,
        out decimal max)
    {
        foreach (var key in new[] { item.ItemInstance, item.CourseModuleId })
        {
            if (!string.IsNullOrWhiteSpace(key) && settings.TryGetValue(key, out max) && max > 0m)
            {
                return true;
            }
        }

        max = 0m;
        return false;
    }

    private static void ParseQuizMaxima(JsonElement payload, IDictionary<string, decimal> result)
    {
        if (!payload.TryGetProperty("quizzes", out var quizzes) || quizzes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var quiz in quizzes.EnumerateArray())
        {
            if (!TryReadPositiveDecimal(quiz, "grade", out var max))
            {
                continue;
            }

            AddId(result, quiz, max, "id", "coursemodule", "cmid");
        }
    }

    private static void ParseAssignmentMaxima(JsonElement payload, IDictionary<string, decimal> result)
    {
        if (!payload.TryGetProperty("courses", out var courses) || courses.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var course in courses.EnumerateArray())
        {
            if (!course.TryGetProperty("assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var assignment in assignments.EnumerateArray())
            {
                if (!TryReadPositiveDecimal(assignment, "grade", out var max))
                {
                    continue;
                }

                AddId(result, assignment, max, "id", "cmid", "coursemodule");
            }
        }
    }

    private static void AddId(
        IDictionary<string, decimal> result,
        JsonElement element,
        decimal value,
        params string[] properties)
    {
        foreach (var property in properties)
        {
            if (TryReadId(element, property, out var id))
            {
                result[id] = value;
            }
        }
    }

    private static bool TryReadPositiveDecimal(JsonElement element, string property, out decimal value)
    {
        value = 0m;
        if (!element.TryGetProperty(property, out var raw))
        {
            return false;
        }

        if (raw.ValueKind == JsonValueKind.Number && raw.TryGetDecimal(out value))
        {
            return value > 0m;
        }

        if (raw.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = raw.GetString();
        return (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
                decimal.TryParse(text, NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out value)) &&
            value > 0m;
    }

    private static bool TryReadId(JsonElement element, string property, out string id)
    {
        id = string.Empty;
        if (!element.TryGetProperty(property, out var raw))
        {
            return false;
        }

        id = raw.ValueKind switch
        {
            JsonValueKind.Number => raw.GetRawText(),
            JsonValueKind.String => raw.GetString() ?? string.Empty,
            _ => string.Empty,
        };
        return !string.IsNullOrWhiteSpace(id);
    }

    private static string ActivityIdentity(GradebookItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Id))
        {
            return item.Id.Trim();
        }

        return string.Join(
            "|",
            ModuleName(item),
            item.ItemInstance?.Trim() ?? string.Empty,
            item.CourseModuleId?.Trim() ?? string.Empty,
            item.ItemName.Trim());
    }

    private static string ModuleName(GradebookItem item)
    {
        var module = item.ItemModule?.Trim();
        if (string.IsNullOrWhiteSpace(module) || string.Equals(module, "mod", StringComparison.OrdinalIgnoreCase))
        {
            module = item.ItemType?.Trim();
        }

        return (module ?? string.Empty).ToLowerInvariant();
    }

    private static bool IsSupportedEvaluativeModule(string module) =>
        module is "quiz" or "assign" or "assignment";

    private static bool IsActivityItem(GradebookItem item) =>
        !IsCourseItem(item) &&
        !string.Equals(item.ItemType, "category", StringComparison.OrdinalIgnoreCase);

    private static bool IsCourseItem(GradebookItem item) =>
        string.Equals(item.ItemType, "course", StringComparison.OrdinalIgnoreCase);

    private static decimal? Positive(decimal? value) => value is > 0m ? value : null;

    private static CourseGradeMaxResolution Empty(string warning) =>
        new(null, null, warning);

    private static string BuildModuleCacheKey(
        MoodleCredentials credentials,
        long courseId,
        IEnumerable<string> modules,
        IEnumerable<GradebookItem> items) =>
        $"moodle-course-grade-max:modules:{credentials.ConnectionId ?? credentials.Alias}:{courseId}:" +
        $"{string.Join(',', modules.OrderBy(module => module, StringComparer.OrdinalIgnoreCase))}:" +
        $"{string.Join(',', items.Select(ActivityIdentity).OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase))}";

    private static long ParseMoodleId(string value, string parameterName)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            return id;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico do Moodle.", parameterName);
    }
}
