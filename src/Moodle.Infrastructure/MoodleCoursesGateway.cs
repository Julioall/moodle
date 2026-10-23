using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain;
using Moodle.Infrastructure.MoodleApi;

namespace Moodle.Infrastructure;

internal sealed class MoodleCoursesGateway(
    IOptions<MoodleApiOptions> options,
    IMemoryCache cache,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient,
    IMoodleFunctionCatalog functionCatalog,
    IMoodleCurrentUserIdGateway currentUserIdGateway,
    IMoodleBusinessFlowRegistry businessFlows,
    IMoodleResourceResolver resourceResolver,
    ILogger<MoodleCoursesGateway>? logger = null) : IMoodleCoursesGateway
{
    private const int MaxUnboundedCategoryEnrichmentCourses = 1_000;
    private const int CourseMetadataBatchSize = 100;
    private readonly MoodleApiOptions _options = options.Value;
    private readonly ILogger<MoodleCoursesGateway> _logger = logger ?? NullLogger<MoodleCoursesGateway>.Instance;
    private static readonly TimeSpan CourseListCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CategoryCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CourseCacheLocks = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<CourseSummary>> GetReportCoursesLiveAsync(string userExternalId,
        IReadOnlyCollection<string> courseIds, bool includeCategoryPath, bool singleAttempt, CancellationToken cancellationToken)
    {
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var userId = await ResolveMoodleUserIdAsync(credentials, userExternalId, cancellationToken);
        var arguments = new Dictionary<string, object?> { ["userid"] = userId.ToString(CultureInfo.InvariantCulture) };
        var payload = singleAttempt
            ? await restClient.CallReadOnceAsync(credentials, "core_enrol_get_users_courses", arguments, cancellationToken)
            : await restClient.CallAsync(credentials, "core_enrol_get_users_courses", arguments, cancellationToken);
        var selected = (JsonSerializer.Deserialize<List<CourseDto>>(payload.GetRawText()) ?? [])
            .Where(c => courseIds.Contains(c.Id.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)).ToArray();
        IReadOnlyDictionary<long, string> paths = new Dictionary<long, string>();
        if (includeCategoryPath)
        {
            var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
            paths = await GetCategoryPathsAsync(credentials, profile,
                selected.Select(c => c.CategoryId ?? c.Category).Where(id => id is > 0).Select(id => id!.Value),
                cancellationToken, bypassCache: true, singleAttempt: singleAttempt);
        }
        return selected.Select(dto =>
        {
            var course = ToCourseSummary(dto);
            var id = dto.CategoryId ?? dto.Category;
            return id.HasValue && paths.TryGetValue(id.Value, out var path)
                ? course with { CategoryId = id, CategoryName = path } : course with { CategoryId = id };
        }).ToArray();
    }

    public async Task<IReadOnlyList<CourseHierarchyNode>> GetMyCourseHierarchyAsync(string userExternalId, CancellationToken cancellationToken)
    {
        var catalog = await GetCachedCoursesAsync(userExternalId, cancellationToken);
        var courses = catalog.Courses;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var course in courses)
        {
            var parts = CourseCategoryDisplay.GetName(course.CategoryId, course.CategoryName).Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var length = 1; length <= parts.Length; length++)
            {
                var path = string.Join(" > ", parts.Take(length));
                counts[path] = counts.TryGetValue(path, out var count) ? count + 1 : 1;
            }
        }

        return counts
            .Select(item => new CourseHierarchyNode(item.Key, item.Key.Split('>').Last().Trim(), item.Key.Count(character => character == '>'), item.Value))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<PagedCourses> GetMyCoursesByCategoryAsync(string userExternalId, string categoryPath, int limit, int page, CancellationToken cancellationToken)
    {
        var catalog = await GetCachedCoursesAsync(userExternalId, cancellationToken);
        var courses = catalog.Courses;
        var filtered = courses.Where(course => string.Equals(CourseCategoryDisplay.GetName(course.CategoryId, course.CategoryName), categoryPath.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var skip = (page - 1) * limit;
        return new PagedCourses(filtered.Skip(skip).Take(limit).ToArray(), filtered.Length, page, limit, catalog.Source);
    }

    public async Task<PagedCourses> GetMyCoursesAsync(
        string userExternalId,
        int limit,
        int page,
        CancellationToken cancellationToken)
    {
        var catalog = await GetCachedCoursesAsync(userExternalId, cancellationToken);
        var courses = catalog.Courses;
        var skip = (page - 1) * limit;
        var items = courses.Skip(skip).Take(limit).ToArray();
        return new PagedCourses(items, courses.Count, page, limit, catalog.Source);
    }

    public async Task<IReadOnlyList<CourseSummary>> SearchMyCoursesAsync(
        string userExternalId,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalizedQuery = query.Trim();
        var reference = resourceResolver.Resolve(normalizedQuery);
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
        var strategy = businessFlows.ResolveStrategy("buscar_cursos", profile);
        if (strategy?.StrategyName == "course_search" && reference.Type is MoodleResourceType.SearchTerm or MoodleResourceType.SearchUrl)
        {
            var searched = await SearchCoursesAsync(credentials, reference.Value, cancellationToken);
            return searched.Take(limit).ToArray();
        }
        if (strategy?.StrategyName == "course_by_field" && reference.Type is MoodleResourceType.CourseId or MoodleResourceType.CourseUrl or MoodleResourceType.IdNumber or MoodleResourceType.ShortName or MoodleResourceType.CategoryId or MoodleResourceType.CategoryUrl)
        {
            var field = GetMoodleField(reference.Type);
            var found = await GetCoursesByFieldAsync(credentials, field, reference.Value, cancellationToken);
            return found.Take(limit).ToArray();
        }

        var catalog = await GetCachedCoursesAsync(userExternalId, cancellationToken);
        var courses = catalog.Courses;

        return courses
            .Where(course => MatchesCourse(course, normalizedQuery))
            .Take(limit)
            .ToArray();
    }

    public async Task<CourseSummary?> GetMyCourseAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(courseId))
        {
            return null;
        }

        var reference = resourceResolver.Resolve(courseId);
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
        var strategy = businessFlows.ResolveStrategy("consultar_curso", profile);
        if (strategy?.StrategyName == "course_by_field" && reference.Type is MoodleResourceType.CourseId or MoodleResourceType.CourseUrl or MoodleResourceType.IdNumber or MoodleResourceType.ShortName)
        {
            var found = await GetCoursesByFieldAsync(credentials, GetMoodleField(reference.Type), reference.Value, cancellationToken);
            var course = found.FirstOrDefault();
            if (course is not null)
            {
                if (await IsEnrolledInCourseAsync(credentials, userExternalId, course.CourseId, profile, cancellationToken))
                {
                    return course;
                }

                throw new MoodleApiException(
                    "not_enrolled",
                    "O curso foi localizado, mas o usuario atual nao possui matricula nele.");
            }
        }

        var catalog = await GetCachedCoursesAsync(userExternalId, cancellationToken);
        var courses = catalog.Courses;
        return courses.FirstOrDefault(course =>
            string.Equals(course.CourseId, courseId.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(course.ShortName, courseId.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(course.IdNumber, courseId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<CourseCatalogRead> GetCachedCoursesAsync(
        string userExternalId,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para fluxos reais. Ajuste a configuracao para usar Moodle real.");
        }

        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var moodleUserId = await ResolveMoodleUserIdAsync(credentials, userExternalId, cancellationToken);
        var cacheKey = $"moodle:courses:{credentials.ConnectionId}:{moodleUserId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<CourseSummary>? cached) && cached is not null)
        {
            return new CourseCatalogRead(cached, CourseReadSource.MemoryCache);
        }

        // Dashboard metrics arrive together. Collapse their cold-cache reads
        // so a large Moodle catalogue is fetched only once per connection.
        var gate = CourseCacheLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached) && cached is not null)
            {
                return new CourseCatalogRead(cached, CourseReadSource.MemoryCache);
            }

            var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
            var moodleCourses = await GetCoursesAsync(credentials, moodleUserId, profile, cancellationToken);
            var categories = await GetCategoryPathsAsync(
                credentials,
                profile,
                moodleCourses.Select(course => course.CategoryId ?? course.Category).Where(id => id is > 0).Select(id => id!.Value),
                cancellationToken);
            _logger.LogInformation("Moodle courses loaded: {CourseCount}, categories: {CategoryCount}", moodleCourses.Count, categories.Count);
            var courses = moodleCourses
                .Select(course =>
                {
                    var categoryId = course.CategoryId ?? course.Category;
                    var categoryName = categoryId is long resolvedCategoryId &&
                                        categories.TryGetValue(resolvedCategoryId, out var resolvedCategoryPath) &&
                                        !string.IsNullOrWhiteSpace(resolvedCategoryPath)
                        ? resolvedCategoryPath
                        : course.CategoryName;

                    return new CourseSummary(
                        course.Id.ToString(CultureInfo.InvariantCulture),
                        course.IdNumber,
                        course.ShortName,
                        course.FullName,
                        course.DisplayName,
                        categoryId,
                        categoryName,
                        ToDateTimeOffset(course.StartDate),
                        ToDateTimeOffset(course.EndDate),
                        ToBool(course.Visible),
                        course.ViewUrl,
                        course.CourseImage,
                        ToDecimal(course.Progress),
                        ToBool(course.HasProgress),
                        ToBool(course.IsFavourite),
                        ToDateTimeOffset(course.TimeAccess));
                })
                .ToArray();
            cache.Set(cacheKey, courses, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CourseListCacheDuration,
                SlidingExpiration = TimeSpan.FromMinutes(3),
            });
            return new CourseCatalogRead(courses, CourseReadSource.Moodle);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<long, string>> GetCategoryPathsAsync(
        MoodleCredentials credentials,
        MoodleFunctionProfile profile,
        IEnumerable<long> requestedCategoryIds,
        CancellationToken cancellationToken,
        bool bypassCache = false,
        bool singleAttempt = false)
    {
        var requestedIds = requestedCategoryIds.Where(id => id > 0).ToHashSet();
        if (requestedIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        if (!IsFunctionAvailable(profile, "core_course_get_categories"))
        {
            _logger.LogWarning(
                "Moodle categories function is unavailable. Courses will remain without category paths. ConnectionKey={ConnectionKey}",
                MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId));
            return new Dictionary<long, string>();
        }

        var cacheKey = $"moodle:course-categories:v2:{credentials.ConnectionId}";
        var byId = !bypassCache && cache.TryGetValue(cacheKey, out IReadOnlyDictionary<long, CategoryDto>? cached) && cached is not null
            ? cached.ToDictionary(item => item.Key, item => item.Value)
            : new Dictionary<long, CategoryDto>();
        var pendingIds = requestedIds.Where(id => !byId.ContainsKey(id)).ToHashSet();
        var failedIds = new HashSet<long>();

        // At most ten rounds of ancestry lookups prevent malformed category paths
        // from causing an unbounded sequence of Moodle calls.
        for (var depth = 0; depth < 10 && pendingIds.Count > 0; depth++)
        {
            var currentIds = pendingIds.Order().ToArray();
            pendingIds.Clear();
            foreach (var idBatch in currentIds.Chunk(CourseMetadataBatchSize))
            {
                var requestedBatch = idBatch.ToHashSet();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var payload = await ReadCategoriesAsync(
                        credentials,
                        "core_course_get_categories",
                        new Dictionary<string, object?>
                        {
                            ["criteria"] = new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["key"] = "ids",
                                    ["value"] = string.Join(',', idBatch),
                                },
                            },
                            ["addsubcategories"] = false,
                        },
                        cancellationToken);
                    var categories = JsonSerializer.Deserialize<IReadOnlyList<CategoryDto>>(payload.GetRawText()) ?? [];
                    var returnedCategories = categories.Where(item => requestedBatch.Contains(item.Id)).ToArray();
                    foreach (var category in returnedCategories)
                    {
                        byId[category.Id] = category;
                    }

                    _logger.LogInformation(
                        "Moodle category metadata batch completed. ConnectionKey={ConnectionKey} RequestedCount={RequestedCount} ReturnedCount={ReturnedCount} DurationMs={DurationMs}",
                        MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                        idBatch.Length,
                        returnedCategories.Length,
                        timer.ElapsedMilliseconds);
                }
                catch (MoodleApiException)
                {
                    failedIds.UnionWith(requestedBatch);
                    _logger.LogWarning(
                        "Moodle category metadata batch failed; preserving resolved categories. ConnectionKey={ConnectionKey} RequestedCount={RequestedCount} DurationMs={DurationMs}",
                        MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                        idBatch.Length,
                        timer.ElapsedMilliseconds);
                }
                catch (JsonException)
                {
                    failedIds.UnionWith(requestedBatch);
                    _logger.LogWarning(
                        "Moodle category metadata batch returned an invalid response. ConnectionKey={ConnectionKey} RequestedCount={RequestedCount} DurationMs={DurationMs}",
                        MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                        idBatch.Length,
                        timer.ElapsedMilliseconds);
                }
            }

            var ancestorIds = byId.Values
                .SelectMany(category => GetCategoryAncestorIds(category))
                .Where(id => !byId.ContainsKey(id) && !failedIds.Contains(id))
                .ToHashSet();
            pendingIds.UnionWith(ancestorIds);
        }

        if (pendingIds.Count > 0)
        {
            _logger.LogWarning(
                "Moodle category ancestry depth limit reached; some category paths will remain pending. ConnectionKey={ConnectionKey} PendingCategoryCount={PendingCategoryCount} MaxDepth={MaxDepth}",
                MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                pendingIds.Count,
                10);
        }

        if (byId.Count > 0 && !bypassCache)
        {
            cache.Set(cacheKey, (IReadOnlyDictionary<long, CategoryDto>)byId, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CategoryCacheDuration,
            });
        }

        Task<JsonElement> ReadCategoriesAsync(MoodleCredentials selectedConnection, string function,
            IReadOnlyDictionary<string, object?> arguments, CancellationToken ct) => singleAttempt
                ? restClient.CallReadOnceAsync(selectedConnection, function, arguments, ct)
                : restClient.CallAsync(selectedConnection, function, arguments, ct);
        var paths = new Dictionary<long, string>();
        string BuildPath(long id, int depth, HashSet<long> visiting)
        {
            if (depth > 10 || !byId.TryGetValue(id, out var category) || !visiting.Add(id))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(category.Path))
            {
                var pathIds = category.Path
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pathId) ? pathId : 0)
                    .ToArray();
                var pathMatchesParents = pathIds.Length > 0 && pathIds[0] > 0 &&
                    byId.TryGetValue(pathIds[0], out var rootCategory) && rootCategory.Parent == 0;
                for (var index = 1; pathMatchesParents && index < pathIds.Length; index++)
                {
                    pathMatchesParents = byId.TryGetValue(pathIds[index], out var pathCategory) &&
                        pathCategory.Parent == pathIds[index - 1];
                }

                if (pathIds.Length is > 0 and <= 10 && pathIds[^1] == id &&
                    pathIds.All(pathId => pathId > 0 && byId.ContainsKey(pathId)) &&
                    pathIds.Distinct().Count() == pathIds.Length && pathMatchesParents)
                {
                    var pathNames = pathIds.Select(pathId => byId[pathId].Name.Trim()).ToArray();
                    var resolvedPath = string.Join(" > ", pathNames);
                    visiting.Remove(id);
                    return resolvedPath;
                }
            }

            var parentPath = category.Parent > 0 ? BuildPath(category.Parent, depth + 1, visiting) : string.Empty;
            visiting.Remove(id);
            if (category.Parent > 0 && string.IsNullOrWhiteSpace(parentPath))
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(parentPath) ? category.Name.Trim() : $"{parentPath} > {category.Name.Trim()}";
        }
        foreach (var id in requestedIds)
        {
            var path = BuildPath(id, 0, []);
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths[id] = path;
            }
        }

        var unresolvedCount = requestedIds.Count - paths.Count;
        if (unresolvedCount > 0)
        {
            _logger.LogWarning(
                "Moodle category paths remain unresolved after filtered lookup. ConnectionKey={ConnectionKey} RequestedCategoryCount={RequestedCategoryCount} ResolvedCategoryCount={ResolvedCategoryCount} PendingCategoryCount={PendingCategoryCount}",
                MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                requestedIds.Count,
                paths.Count,
                unresolvedCount);
        }

        return paths;
    }

    private static IEnumerable<long> GetCategoryAncestorIds(CategoryDto category)
    {
        if (category.Parent > 0 && category.Parent != category.Id)
        {
            yield return category.Parent;
        }

        if (!string.IsNullOrWhiteSpace(category.Path))
        {
            foreach (var value in category.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 && id != category.Id)
                {
                    yield return id;
                }
            }
        }
    }

    private async Task<IReadOnlyList<CourseDto>> GetCoursesAsync(
        MoodleCredentials credentials,
        long moodleUserId,
        MoodleFunctionProfile profile,
        CancellationToken cancellationToken)
    {
        var strategy = businessFlows.ResolveStrategy("listar_cursos_ativos", profile);
        if (strategy?.StrategyName == "timeline")
        {
            // Prefer the timeline endpoint for large catalogues. Unlike
            // core_enrol_get_users_courses, it supports limit/offset and avoids
            // one unbounded response for users enrolled in many courses.
            try
            {
                var timelineCourses = await GetTimelineCoursesAsync(credentials, cancellationToken);
                if (timelineCourses.Count > 0)
                {
                    if (RequiresTimelineCategoryEnrichment(timelineCourses) &&
                        timelineCourses.Count <= MaxUnboundedCategoryEnrichmentCourses &&
                        IsFunctionAvailable(profile, "core_enrol_get_users_courses"))
                    {
                        try
                        {
                            var enrolledCourses = await GetEnrolledCoursesAsync(credentials, moodleUserId, cancellationToken);
                            var enrichedCourses = MergeCourseCategoryData(timelineCourses, enrolledCourses);
                            if (!ReferenceEquals(enrichedCourses, timelineCourses))
                            {
                                _logger.LogInformation(
                                    "Moodle timeline courses were enriched with enrolled-course category data. ConnectionKey={ConnectionKey}",
                                    MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId));
                            }

                            timelineCourses = enrichedCourses;
                        }
                        catch (MoodleApiException)
                        {
                            _logger.LogWarning("Moodle enrolled-course category enrichment failed; trying bounded course metadata reads.");
                        }
                    }
                    else if (RequiresTimelineCategoryEnrichment(timelineCourses) &&
                             timelineCourses.Count > MaxUnboundedCategoryEnrichmentCourses)
                    {
                        _logger.LogWarning(
                            "Skipping unbounded enrolled-course category enrichment for a large catalogue. ConnectionKey={ConnectionKey} CourseCount={CourseCount}",
                            MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                            timelineCourses.Count);
                    }

                    if (RequiresTimelineCategoryEnrichment(timelineCourses))
                    {
                        if (IsFunctionAvailable(profile, "core_course_get_courses"))
                        {
                            timelineCourses = await EnrichTimelineCourseCategoriesAsync(
                                credentials,
                                timelineCourses,
                                cancellationToken);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Moodle course metadata function is unavailable for this connection; keeping category metadata pending. ConnectionKey={ConnectionKey} CourseCount={CourseCount}",
                                MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                                timelineCourses.Count);
                        }
                    }

                    return timelineCourses;
                }
            }
            catch (MoodleApiException)
            {
                _logger.LogWarning("Moodle timeline course listing unavailable; trying enrolled courses fallback.");
            }

            if (IsFunctionAvailable(profile, "core_enrol_get_users_courses"))
            {
                try
                {
                    var enrolledCourses = await GetEnrolledCoursesAsync(credentials, moodleUserId, cancellationToken);
                    if (enrolledCourses.Count > 0)
                    {
                        return enrolledCourses;
                    }
                }
                catch (MoodleApiException)
                {
                    _logger.LogWarning("Moodle enrolled courses fallback unavailable.");
                }
            }

            if (!IsFunctionAvailable(profile, "core_course_get_enrolled_courses_by_timeline_classification"))
            {
                throw new MoodleApiException(
                    "flow_unavailable",
                    "A conexão Moodle não possui uma função compatível para listar os cursos matriculados.");
            }

            return await GetTimelineCoursesAsync(credentials, cancellationToken);
        }

        if (strategy?.StrategyName != "enrolled_courses_fallback")
        {
            throw new MoodleApiException(
                "flow_unavailable",
                "O fluxo listar_cursos_ativos nao possui uma estrategia compativel com as funcoes habilitadas nesta conexao Moodle.");
        }

        return await GetEnrolledCoursesAsync(credentials, moodleUserId, cancellationToken);
    }

    private async Task<IReadOnlyList<CourseDto>> GetEnrolledCoursesAsync(
        MoodleCredentials credentials,
        long moodleUserId,
        CancellationToken cancellationToken)
    {
        var payload = await restClient.CallAsync(
            credentials,
            "core_enrol_get_users_courses",
            new Dictionary<string, string>
            {
                ["userid"] = moodleUserId.ToString(CultureInfo.InvariantCulture)
            }.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
            cancellationToken);

        return JsonSerializer.Deserialize<List<CourseDto>>(payload.GetRawText()) ?? [];
    }

    private async Task<IReadOnlyList<CourseDto>> GetTimelineCoursesAsync(
        MoodleCredentials credentials,
        CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        const int maxPages = 100;
        var courses = new List<CourseDto>();
        var seenCourseIds = new HashSet<int>();
        var offset = 0;

        for (var page = 0; page < maxPages; page++)
        {
            var timelinePayload = await restClient.CallAsync(
                credentials,
                "core_course_get_enrolled_courses_by_timeline_classification",
                new Dictionary<string, object?>
                {
                    // Moodle scopes this response to the authenticated user. Use
                    // all classifications, but page the response so large
                    // connections do not produce one giant JSON payload.
                    ["classification"] = "all",
                    ["limit"] = pageSize,
                    ["offset"] = offset,
                    ["sort"] = "fullname"
                },
                cancellationToken);
            var timeline = JsonSerializer.Deserialize<TimelineCoursesResponseDto>(timelinePayload.GetRawText());
            var pageCourses = timeline?.Courses ?? [];
            if (pageCourses.Count == 0)
            {
                break;
            }

            var newCourses = pageCourses.Where(course => seenCourseIds.Add(course.Id)).ToArray();
            courses.AddRange(newCourses);
            var nextOffset = offset + pageCourses.Count;
            if (newCourses.Length == 0 ||
                pageCourses.Count < pageSize ||
                timeline?.Total is { } total && total <= nextOffset)
            {
                break;
            }

            offset = nextOffset;
        }

        if (courses.Count > 0)
        {
            _logger.LogInformation(
                "Moodle timeline courses loaded in pages. ConnectionKey={ConnectionKey} CourseCount={CourseCount}",
                MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                courses.Count);
        }

        return courses;
    }

    private static bool IsFunctionAvailable(MoodleFunctionProfile profile, string functionName) =>
        profile.Functions.Any(function =>
            function.IsAvailable &&
            string.Equals(function.Name, functionName, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<CourseSummary>> GetCoursesByFieldAsync(
        MoodleCredentials credentials,
        string field,
        string value,
        CancellationToken cancellationToken)
    {
        var payload = await restClient.CallAsync(
            credentials,
            "core_course_get_courses_by_field",
            new Dictionary<string, object?> { ["field"] = field, ["value"] = value },
            cancellationToken);
        var response = JsonSerializer.Deserialize<CoursesByFieldResponseDto>(payload.GetRawText());
        return (response?.Courses ?? []).Select(ToCourseSummary).ToArray();
    }

    private async Task<IReadOnlyList<CourseSummary>> SearchCoursesAsync(
        MoodleCredentials credentials,
        string query,
        CancellationToken cancellationToken)
    {
        var payload = await restClient.CallAsync(
            credentials,
            "core_course_search_courses",
            new Dictionary<string, object?> { ["criterianame"] = "search", ["criteriavalue"] = query },
            cancellationToken);
        var response = JsonSerializer.Deserialize<CourseSearchResponseDto>(payload.GetRawText());
        return (response?.Courses ?? []).Select(ToCourseSummary).ToArray();
    }

    private async Task<bool> IsEnrolledInCourseAsync(
        MoodleCredentials credentials,
        string userExternalId,
        string courseId,
        MoodleFunctionProfile profile,
        CancellationToken cancellationToken)
    {
        if (int.TryParse(userExternalId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moodleUserId) &&
            long.TryParse(courseId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCourseId) &&
            profile.Functions.Any(function => function.IsAvailable && string.Equals(function.Name, "core_enrol_get_enrolled_users", StringComparison.OrdinalIgnoreCase)))
        {
            var payload = await restClient.CallAsync(
                credentials,
                "core_enrol_get_enrolled_users",
                new Dictionary<string, object?> { ["courseid"] = parsedCourseId },
                cancellationToken);
            var enrolledUsers = JsonSerializer.Deserialize<IReadOnlyList<EnrolledUserDto>>(payload.GetRawText()) ?? [];
            return enrolledUsers.Any(user => user.Id == moodleUserId);
        }

        var enrolledCourses = await GetCachedCoursesAsync(userExternalId, cancellationToken);
        return enrolledCourses.Courses.Any(course => string.Equals(course.CourseId, courseId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CourseCatalogRead(
        IReadOnlyList<CourseSummary> Courses,
        CourseReadSource Source);

    private async Task<long> ResolveMoodleUserIdAsync(MoodleCredentials credentials, string userExternalId, CancellationToken cancellationToken)
    {
        if (long.TryParse(userExternalId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moodleUserId))
        {
            return moodleUserId;
        }

        return await currentUserIdGateway.GetCurrentUserIdAsync(cancellationToken);
    }

    private static DateTimeOffset? ToDateTimeOffset(JsonElement value)
    {
        var seconds = ToInt64(value);
        return seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
    }

    private static long? ToInt64(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static decimal? ToDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static bool? ToBool(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var boolean) => boolean,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number != 0,
            _ => null
        };
    }

    private static bool MatchesCourse(CourseSummary course, string query)
    {
        return Contains(course.CourseId, query) ||
               Contains(course.IdNumber, query) ||
               Contains(course.ShortName, query) ||
               Contains(course.FullName, query) ||
               Contains(course.DisplayName, query) ||
               Contains(course.CategoryName, query);
    }

    private static bool Contains(string? value, string query)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMoodleField(MoodleResourceType type) => type switch
    {
        MoodleResourceType.CourseId or MoodleResourceType.CourseUrl => "id",
        MoodleResourceType.IdNumber => "idnumber",
        MoodleResourceType.ShortName => "shortname",
        MoodleResourceType.CategoryId or MoodleResourceType.CategoryUrl => "category",
        _ => "id"
    };

    private static bool RequiresTimelineCategoryEnrichment(IReadOnlyList<CourseDto> courses) =>
        courses.Any(course => (course.CategoryId ?? course.Category) is null);

    private async Task<IReadOnlyList<CourseDto>> EnrichTimelineCourseCategoriesAsync(
        MoodleCredentials credentials,
        IReadOnlyList<CourseDto> courses,
        CancellationToken cancellationToken)
    {
        var coursesById = courses.ToDictionary(course => course.Id);
        var missingIds = courses
            .Where(course => (course.CategoryId ?? course.Category) is null)
            .Select(course => course.Id)
            .Distinct()
            .ToArray();
        var enrichedCount = 0;

        foreach (var idBatch in missingIds.Chunk(CourseMetadataBatchSize))
        {
            var requestedIds = idBatch.ToHashSet();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var payload = await restClient.CallAsync(
                    credentials,
                    "core_course_get_courses",
                    new Dictionary<string, object?>
                    {
                        ["options"] = new Dictionary<string, object?>
                        {
                            ["ids"] = idBatch,
                        },
                    },
                    cancellationToken);
                var metadata = JsonSerializer.Deserialize<IReadOnlyList<CourseDto>>(payload.GetRawText()) ?? [];
                var requestedMetadata = metadata
                    .Where(course => requestedIds.Contains(course.Id))
                    .GroupBy(course => course.Id)
                    .ToDictionary(group => group.Key, group => group.First());
                var batchEnrichedCount = 0;

                foreach (var id in idBatch)
                {
                    if (!coursesById.TryGetValue(id, out var course) ||
                        !requestedMetadata.TryGetValue(id, out var fallback))
                    {
                        continue;
                    }

                    var enriched = MergeCourseCategoryData(course, fallback);
                    if (!ReferenceEquals(enriched, course))
                    {
                        coursesById[id] = enriched;
                        enrichedCount++;
                        batchEnrichedCount++;
                    }
                }

                _logger.LogInformation(
                    "Moodle course category metadata batch completed. ConnectionKey={ConnectionKey} BatchSize={BatchSize} EnrichedCount={EnrichedCount} DurationMs={DurationMs}",
                    MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                    idBatch.Length,
                    batchEnrichedCount,
                    timer.ElapsedMilliseconds);
            }
            catch (MoodleApiException)
            {
                _logger.LogWarning(
                    "Moodle course category metadata batch failed; keeping listed courses. ConnectionKey={ConnectionKey} BatchSize={BatchSize} DurationMs={DurationMs}",
                    MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                    idBatch.Length,
                    timer.ElapsedMilliseconds);
            }
            catch (JsonException)
            {
                _logger.LogWarning(
                    "Moodle course category metadata batch returned an invalid response; keeping listed courses. ConnectionKey={ConnectionKey} BatchSize={BatchSize} DurationMs={DurationMs}",
                    MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                    idBatch.Length,
                    timer.ElapsedMilliseconds);
            }
        }

        if (enrichedCount > 0)
        {
            _logger.LogInformation(
                "Moodle course category metadata enrichment finished. ConnectionKey={ConnectionKey} EnrichedCount={EnrichedCount} PendingCount={PendingCount}",
                MoodleCallDiagnosticCatalog.ConnectionKey(credentials.ConnectionId),
                enrichedCount,
                Math.Max(0, missingIds.Length - enrichedCount));
        }

        return courses.Select(course => coursesById[course.Id]).ToArray();
    }

    private static IReadOnlyList<CourseDto> MergeCourseCategoryData(
        IReadOnlyList<CourseDto> primaryCourses,
        IReadOnlyList<CourseDto> fallbackCourses)
    {
        if (primaryCourses.Count == 0 || fallbackCourses.Count == 0)
        {
            return primaryCourses;
        }

        var fallbackById = fallbackCourses
            .GroupBy(course => course.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var changed = false;
        var merged = new CourseDto[primaryCourses.Count];

        for (var index = 0; index < primaryCourses.Count; index++)
        {
            var course = primaryCourses[index];
            if (!fallbackById.TryGetValue(course.Id, out var fallback))
            {
                merged[index] = course;
                continue;
            }

            var enriched = MergeCourseCategoryData(course, fallback);
            changed |= !ReferenceEquals(enriched, course);
            merged[index] = enriched;
        }

        return changed ? merged : primaryCourses;
    }

    private static CourseDto MergeCourseCategoryData(CourseDto course, CourseDto fallback)
    {
        var categoryId = course.CategoryId ?? fallback.CategoryId;
        var category = course.Category ?? fallback.Category;
        var categoryName = !string.IsNullOrWhiteSpace(course.CategoryName)
            ? course.CategoryName
            : fallback.CategoryName;

        if (categoryId == course.CategoryId &&
            category == course.Category &&
            string.Equals(categoryName, course.CategoryName, StringComparison.Ordinal))
        {
            return course;
        }

        return new CourseDto
        {
            Id = course.Id,
            ShortName = course.ShortName,
            IdNumber = course.IdNumber,
            FullName = course.FullName,
            DisplayName = course.DisplayName,
            CategoryId = categoryId,
            Category = category,
            CategoryName = categoryName,
            StartDate = course.StartDate,
            EndDate = course.EndDate,
            Visible = course.Visible,
            ViewUrl = course.ViewUrl,
            CourseImage = course.CourseImage,
            Progress = course.Progress,
            HasProgress = course.HasProgress,
            IsFavourite = course.IsFavourite,
            TimeAccess = course.TimeAccess
        };
    }

    private static CourseSummary ToCourseSummary(CourseDto course) => new(
        course.Id.ToString(CultureInfo.InvariantCulture),
        course.IdNumber,
        course.ShortName,
        course.FullName,
        course.DisplayName,
        course.CategoryId,
        course.CategoryName,
        ToDateTimeOffset(course.StartDate),
        ToDateTimeOffset(course.EndDate),
        ToBool(course.Visible),
        course.ViewUrl,
        course.CourseImage,
        ToDecimal(course.Progress),
        ToBool(course.HasProgress),
        ToBool(course.IsFavourite),
        ToDateTimeOffset(course.TimeAccess));

    private sealed class CourseDto
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("shortname")]
        public string? ShortName { get; init; }

        [JsonPropertyName("idnumber")]
        public string? IdNumber { get; init; }

        [JsonPropertyName("fullname")]
        public string FullName { get; init; } = string.Empty;

        [JsonPropertyName("displayname")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("categoryid")]
        public long? CategoryId { get; init; }

        [JsonPropertyName("category")]
        public long? Category { get; init; }

        [JsonPropertyName("categoryname")]
        public string? CategoryName { get; init; }

        [JsonPropertyName("startdate")]
        public JsonElement StartDate { get; init; }

        [JsonPropertyName("enddate")]
        public JsonElement EndDate { get; init; }

        [JsonPropertyName("visible")]
        public JsonElement Visible { get; init; }

        [JsonPropertyName("viewurl")]
        public string? ViewUrl { get; init; }

        [JsonPropertyName("courseimage")]
        public string? CourseImage { get; init; }

        [JsonPropertyName("progress")]
        public JsonElement Progress { get; init; }

        [JsonPropertyName("hasprogress")]
        public JsonElement HasProgress { get; init; }

        [JsonPropertyName("isfavourite")]
        public JsonElement IsFavourite { get; init; }

        [JsonPropertyName("timeaccess")]
        public JsonElement TimeAccess { get; init; }
    }

    private sealed class CategoryDto
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("parent")]
        public long Parent { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }
    }

    private sealed class CoursesByFieldResponseDto
    {
        [JsonPropertyName("courses")]
        public IReadOnlyList<CourseDto>? Courses { get; init; }
    }

    private sealed class CourseSearchResponseDto
    {
        [JsonPropertyName("courses")]
        public IReadOnlyList<CourseDto>? Courses { get; init; }
    }

    private sealed class TimelineCoursesResponseDto
    {
        [JsonPropertyName("courses")]
        public IReadOnlyList<CourseDto>? Courses { get; init; }

        [JsonPropertyName("total")]
        public int? Total { get; init; }
    }

    private sealed class EnrolledUserDto
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }
    }
}
