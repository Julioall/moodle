using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Infrastructure;

internal sealed class MoodleAssignmentSettingsGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient,
    IMemoryCache memoryCache) : IMoodleAssignmentSettingsGateway
{
    private const string AssignmentGradingArea = "submissions";
    private readonly MoodleApiOptions _options = options.Value;

    public async Task<AssignmentSettingsSummary?> GetAssignmentSettingsAsync(
        string userExternalId,
        string courseId,
        string assignmentId,
        CancellationToken cancellationToken) =>
        await GetAssignmentSettingsCoreAsync(userExternalId, courseId, assignmentId, forceRefresh: false, cancellationToken);

    public async Task<AssignmentSettingsSummary?> GetAssignmentSettingsFreshAsync(
        string userExternalId,
        string courseId,
        string assignmentId,
        CancellationToken cancellationToken) =>
        await GetAssignmentSettingsCoreAsync(userExternalId, courseId, assignmentId, forceRefresh: true, cancellationToken);

    public async Task<GradingRubricSnapshot?> GetActiveRubricFreshAsync(
        string userExternalId,
        long courseModuleId,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            return null;
        }

        if (courseModuleId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(courseModuleId));
        }

        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var payload = await restClient.CallAsync(
            credentials,
            "core_grading_get_definitions",
            new Dictionary<string, object?>
            {
                ["cmids"] = new[] { courseModuleId },
                ["areaname"] = AssignmentGradingArea,
                ["activeonly"] = true
            },
            cancellationToken);

        return ParseActiveRubric(payload, courseModuleId);
    }

    private async Task<AssignmentSettingsSummary?> GetAssignmentSettingsCoreAsync(
        string userExternalId,
        string courseId,
        string assignmentId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            // The local stub does not model the assignment grading scale. Do not
            // manufacture a numeric maximum: callers must keep numeric grading
            // blocked until a real, verifiable Moodle scale is available.
            return new AssignmentSettingsSummary(assignmentId, 0m, Name: null, IsGradable: null);
        }

        var normalizedCourseId = ParseMoodleId(courseId, nameof(courseId));
        var normalizedAssignmentId = ParseMoodleId(assignmentId, nameof(assignmentId));
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var cachePrefix = BuildCachePrefix(credentials, userExternalId);

        var cacheKey = $"{cachePrefix}:assignment:{normalizedCourseId}:{normalizedAssignmentId}";
        if (!forceRefresh && memoryCache.TryGetValue(cacheKey, out AssignmentSettingsSummary? cached) && cached is not null)
        {
            return cached;
        }

        var parameters = new Dictionary<string, string>
        {
            ["courseids[0]"] = normalizedCourseId.ToString(CultureInfo.InvariantCulture)
        };

        var payload = await restClient.CallAsync(
            credentials,
            "mod_assign_get_assignments",
            parameters.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
            cancellationToken);
        var settings = ParseSettings(payload.GetRawText(), normalizedAssignmentId, normalizedCourseId);

        if (settings is not null)
        {
            settings = settings with { SourceConnectionId = credentials.ConnectionId };
            memoryCache.Set(cacheKey, settings, TimeSpan.FromMinutes(15));
        }

        return settings;
    }

    public async Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken) =>
        await GetCourseAssignmentSettingsCoreAsync(userExternalId, courseId, forceRefresh: false, cancellationToken);

    public async Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsFreshAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken)
        => await GetCourseAssignmentSettingsCoreAsync(
            userExternalId,
            courseId,
            forceRefresh: true,
            cancellationToken);

    private async Task<IReadOnlyDictionary<string, AssignmentSettingsSummary>> GetCourseAssignmentSettingsCoreAsync(
        string userExternalId,
        string courseId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            return new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal);
        }

        var normalizedCourseId = ParseMoodleId(courseId, nameof(courseId));
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var cachePrefix = BuildCachePrefix(credentials, userExternalId);
        var cacheKey = $"{cachePrefix}:course:{normalizedCourseId}";
        if (!forceRefresh && memoryCache.TryGetValue(
                cacheKey,
                out IReadOnlyDictionary<string, AssignmentSettingsSummary>? cached) && cached is not null)
        {
            return cached;
        }

        var parameters = new Dictionary<string, string>
        {
            ["courseids[0]"] = normalizedCourseId.ToString(CultureInfo.InvariantCulture)
        };

        var payload = await restClient.CallAsync(
            credentials,
            "mod_assign_get_assignments",
            parameters.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
            cancellationToken);
        var settingsById = ParseAllSettings(payload.GetRawText(), normalizedCourseId).ToDictionary(
            pair => pair.Key, pair => pair.Value with { SourceConnectionId = credentials.ConnectionId }, StringComparer.Ordinal);

        if (!forceRefresh)
        {
            memoryCache.Set(cacheKey, settingsById, TimeSpan.FromMinutes(15));
            foreach (var pair in settingsById)
            {
                memoryCache.Set(
                    $"{cachePrefix}:assignment:{normalizedCourseId}:{pair.Key}",
                    pair.Value,
                    TimeSpan.FromMinutes(15));
            }
        }

        return settingsById;
    }

    private static string BuildCachePrefix(
        MoodleCredentials credentials,
        string userExternalId)
    {
        // Assignment visibility/settings can vary by authenticated Moodle
        // connection and actor. Keep every cache namespace tenant-safe; a
        // numeric course/assignment id is not globally unique across sites.
        static string Part(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "_" : value.Trim().Replace(":", "%3A", StringComparison.Ordinal);

        return $"moodle_assign_settings:{Part(credentials.ClientId)}:{Part(credentials.ConnectionId)}:{Part(credentials.Alias)}:{Part(userExternalId)}";
    }

    private static AssignmentSettingsSummary? ParseSettings(string payload, long assignmentId, long requestedCourseId)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("courses", out var courses) ||
            courses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var course in courses.EnumerateArray())
        {
            if (ReadId(course, "id") is > 0 and var returnedCourseId && returnedCourseId != requestedCourseId) continue;
            if (!course.TryGetProperty("assignments", out var assignments) || assignments.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var assignment in assignments.EnumerateArray())
            {
                if (!assignment.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                var currentId = idElement.ValueKind == JsonValueKind.Number ? idElement.GetInt64() :
                                long.TryParse(idElement.GetString(), out var idParsed) ? idParsed : -1;

                // Also check cmid just in case the provided assignmentId was the module id instead of instance id
                var cmid = -1L;
                if (assignment.TryGetProperty("cmid", out var cmidElement))
                {
                    cmid = cmidElement.ValueKind == JsonValueKind.Number ? cmidElement.GetInt64() :
                           long.TryParse(cmidElement.GetString(), out var cmidParsed) ? cmidParsed : -1;
                }

                if (currentId == assignmentId || cmid == assignmentId)
                {
                    // Parse assignment name
                    string? assignmentName = null;
                    if (assignment.TryGetProperty("name", out var nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String)
                    {
                        assignmentName = nameElement.GetString();
                    }
                    var assignmentDescription = ReadString(assignment, "intro");
                    decimal grade = 0m;
                    var hasGrade = assignment.TryGetProperty("grade", out var gradeElement) &&
                        TryReadDecimal(gradeElement, out grade);
                    var introAttachments = ReadIntroAttachments(assignment, out var introAttachmentsComplete);

                    if (hasGrade)
                    {
                        // Moodle retorna grade negativo para indicar que a atividade usa escala
                        // (o valor absoluto é o ID da escala). Nesse caso, MaxGrade fica 0
                        // pois não temos conversão de escala implementada.
                        var effectiveMaxGrade = grade > 0 ? grade : 0m;

                        return EnrichOfficial(new AssignmentSettingsSummary(
                            currentId.ToString(CultureInfo.InvariantCulture),
                            effectiveMaxGrade,
                            assignmentName,
                            IsGradable: hasGrade ? grade != 0m : null,
                            Description: assignmentDescription,
                            RawGrade: hasGrade ? grade : null,
                            GradeScaleId: hasGrade ? ResolveScaleId(grade) : null,
                            IntroAttachments: introAttachments,
                            IntroAttachmentsComplete: introAttachmentsComplete,
                            CourseModuleId: cmid > 0 ? cmid : null,
                            MarkingWorkflowEnabled: ReadNullableBoolean(assignment, "markingworkflow")), assignment);
                    }

                    // Preserve available official content even when grading/name is omitted.
                    return EnrichOfficial(new AssignmentSettingsSummary(
                            currentId.ToString(CultureInfo.InvariantCulture),
                            MaxGrade: 0m,
                            assignmentName,
                            IsGradable: null,
                            Description: assignmentDescription,
                            IntroAttachments: introAttachments,
                            IntroAttachmentsComplete: introAttachmentsComplete,
                            CourseModuleId: cmid > 0 ? cmid : null,
                            MarkingWorkflowEnabled: ReadNullableBoolean(assignment, "markingworkflow")), assignment);
                }
            }
        }

        return null;
    }

    internal static GradingRubricSnapshot ParseActiveRubric(JsonElement payload, long expectedCourseModuleId)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("areas", out var areas) ||
            areas.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("A resposta Moodle de definições de correção não contém areas.");
        }

        var warnings = payload.TryGetProperty("warnings", out var warningElement) &&
            warningElement.ValueKind == JsonValueKind.Array && warningElement.GetArrayLength() > 0;
        var matchingAreas = areas.EnumerateArray()
            .Where(area => ReadId(area, "cmid") == expectedCourseModuleId &&
                string.Equals(ReadString(area, "component"), "mod_assign", StringComparison.Ordinal) &&
                string.Equals(ReadString(area, "areaname"), AssignmentGradingArea, StringComparison.Ordinal))
            .ToArray();

        if (matchingAreas.Length != 1)
        {
            return new GradingRubricSnapshot(
                null,
                "moodle:core_grading_get_definitions",
                CourseModuleId: expectedCourseModuleId,
                AreaName: AssignmentGradingArea,
                AssociationStatus: warnings ? "unavailable" : "unresolved");
        }

        var area = matchingAreas[0];
        var activeMethod = ReadString(area, "activemethod");
        if (warnings)
        {
            return new GradingRubricSnapshot(
                null,
                "moodle:core_grading_get_definitions",
                CourseModuleId: expectedCourseModuleId,
                AreaName: AssignmentGradingArea,
                Method: activeMethod,
                AssociationStatus: "unavailable");
        }

        if (!string.Equals(activeMethod, "rubric", StringComparison.OrdinalIgnoreCase))
        {
            return new GradingRubricSnapshot(
                null,
                "moodle:core_grading_get_definitions",
                CourseModuleId: expectedCourseModuleId,
                AreaName: AssignmentGradingArea,
                Method: activeMethod,
                AssociationStatus: "no_active_rubric");
        }

        if (!area.TryGetProperty("definitions", out var definitions) || definitions.ValueKind != JsonValueKind.Array)
        {
            return new GradingRubricSnapshot(
                null,
                "moodle:core_grading_get_definitions",
                CourseModuleId: expectedCourseModuleId,
                AreaName: AssignmentGradingArea,
                Method: activeMethod,
                AssociationStatus: "unavailable");
        }

        var activeDefinitions = definitions.EnumerateArray()
            .Where(definition => string.Equals(ReadString(definition, "method"), "rubric", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (activeDefinitions.Length != 1)
        {
            return new GradingRubricSnapshot(
                null,
                "moodle:core_grading_get_definitions",
                CourseModuleId: expectedCourseModuleId,
                AreaName: AssignmentGradingArea,
                Method: activeMethod,
                AssociationStatus: "ambiguous");
        }

        var definition = activeDefinitions[0];
        var definitionId = ReadId(definition, "id");
        if (definitionId <= 0 ||
            !definition.TryGetProperty("rubric_criteria", out var criterionElements) ||
            criterionElements.ValueKind != JsonValueKind.Array || criterionElements.GetArrayLength() == 0)
        {
            return new GradingRubricSnapshot(
                ReadString(definition, "description"),
                "moodle:core_grading_get_definitions",
                DefinitionId: definitionId > 0 ? definitionId : null,
                CourseModuleId: expectedCourseModuleId,
                AreaName: AssignmentGradingArea,
                Method: activeMethod,
                AssociationStatus: "unsupported");
        }

        var criteria = new List<GradingRubricCriterionSnapshot>();
        foreach (var criterion in criterionElements.EnumerateArray())
        {
            var criterionId = ReadId(criterion, "id");
            if (criterionId <= 0 ||
                !criterion.TryGetProperty("levels", out var levelElements) ||
                levelElements.ValueKind != JsonValueKind.Array || levelElements.GetArrayLength() == 0)
            {
                return UnsupportedRubric(definition, definitionId, expectedCourseModuleId);
            }

            var levels = new List<GradingRubricLevelSnapshot>();
            foreach (var level in levelElements.EnumerateArray())
            {
                var levelId = ReadId(level, "id");
                if (levelId <= 0 || !TryReadDecimal(level, "score", out var score) || score < 0)
                {
                    return UnsupportedRubric(definition, definitionId, expectedCourseModuleId);
                }

                levels.Add(new GradingRubricLevelSnapshot(
                    levelId.ToString(CultureInfo.InvariantCulture),
                    score,
                    ReadString(level, "definition")));
            }

            var criterionScores = levels.Select(level => level.Score).ToArray();
            criteria.Add(new GradingRubricCriterionSnapshot(
                criterionId.ToString(CultureInfo.InvariantCulture),
                ReadString(criterion, "description") ?? string.Empty,
                criterionScores.Min(),
                criterionScores.Max(),
                Array.AsReadOnly(levels.ToArray())));
        }

        if (criteria.Select(criterion => criterion.CriterionId).Distinct(StringComparer.Ordinal).Count() != criteria.Count)
        {
            return UnsupportedRubric(definition, definitionId, expectedCourseModuleId);
        }

        var orderedCriteria = criteria.ToArray();
        var minimumPoints = orderedCriteria.Sum(criterion => criterion.MinimumPoints);
        var maximumPoints = orderedCriteria.Sum(criterion => criterion.MaximumPoints);
        var definitionVersion = ComputeRubricDefinitionVersion(
            definitionId,
            ReadString(definition, "name"),
            ReadString(definition, "description"),
            orderedCriteria,
            minimumPoints,
            maximumPoints);

        return new GradingRubricSnapshot(
            ReadString(definition, "description"),
            "moodle:core_grading_get_definitions",
            DefinitionId: definitionId,
            CourseModuleId: expectedCourseModuleId,
            AreaName: AssignmentGradingArea,
            Method: "rubric",
            AssociationStatus: "active",
            DefinitionVersion: definitionVersion,
            MinimumPoints: minimumPoints,
            MaximumPoints: maximumPoints,
            Criteria: Array.AsReadOnly(orderedCriteria));
    }

    private static GradingRubricSnapshot UnsupportedRubric(
        JsonElement definition,
        long definitionId,
        long courseModuleId) => new(
            ReadString(definition, "description"),
            "moodle:core_grading_get_definitions",
            DefinitionId: definitionId > 0 ? definitionId : null,
            CourseModuleId: courseModuleId,
            AreaName: AssignmentGradingArea,
            Method: "rubric",
            AssociationStatus: "unsupported");

    private static string ComputeRubricDefinitionVersion(
        long definitionId,
        string? name,
        string? description,
        IReadOnlyList<GradingRubricCriterionSnapshot> criteria,
        decimal minimumPoints,
        decimal maximumPoints)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            definitionId,
            method = "rubric",
            area = AssignmentGradingArea,
            name,
            description,
            minimumPoints,
            maximumPoints,
            criteria = criteria
                .OrderBy(criterion => criterion.CriterionId, StringComparer.Ordinal)
                .Select(criterion => new
            {
                criterion.CriterionId,
                criterion.Description,
                criterion.MinimumPoints,
                criterion.MaximumPoints,
                levels = criterion.Levels
                    .OrderBy(level => level.LevelId, StringComparer.Ordinal)
                    .Select(level => new
                {
                    level.LevelId,
                    level.Score,
                    level.Description
                })
            })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool TryReadDecimal(JsonElement element, string propertyName, out decimal result)
    {
        result = 0m;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetDecimal(out result);
        }

        return value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static IReadOnlyDictionary<string, AssignmentSettingsSummary> ParseAllSettings(string payload, long requestedCourseId)
    {
        var result = new Dictionary<string, AssignmentSettingsSummary>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return result;
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("courses", out var courses) ||
            courses.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var course in courses.EnumerateArray())
        {
            if (ReadId(course, "id") is > 0 and var returnedCourseId && returnedCourseId != requestedCourseId) continue;
            if (!course.TryGetProperty("assignments", out var assignments) ||
                assignments.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var assignment in assignments.EnumerateArray())
            {
                var assignmentId = ReadId(assignment, "id");
                if (assignmentId <= 0)
                {
                    continue;
                }

                var cmid = ReadId(assignment, "cmid");
                var name = ReadString(assignment, "name");
                var description = ReadString(assignment, "intro");
                decimal grade = 0m;
                var hasGrade = assignment.TryGetProperty("grade", out var gradeElement) &&
                    TryReadDecimal(gradeElement, out grade);
                var introAttachments = ReadIntroAttachments(assignment, out var introAttachmentsComplete);
                var settings = EnrichOfficial(new AssignmentSettingsSummary(
                    assignmentId.ToString(CultureInfo.InvariantCulture),
                    hasGrade && grade > 0 ? grade : 0m,
                    name,
                    IsGradable: hasGrade ? grade != 0m : null,
                    Description: description,
                    RawGrade: hasGrade ? grade : null,
                    GradeScaleId: hasGrade ? ResolveScaleId(grade) : null,
                    IntroAttachments: introAttachments,
                    IntroAttachmentsComplete: introAttachmentsComplete,
                    CourseModuleId: cmid > 0 ? cmid : null,
                    MarkingWorkflowEnabled: ReadNullableBoolean(assignment, "markingworkflow")), assignment);

                result[assignmentId.ToString(CultureInfo.InvariantCulture)] = settings;
            }
        }

        // Canonical instance IDs take precedence over aliases: cmid and instance
        // belong to different Moodle tables and may numerically collide.
        foreach (var setting in result.Values.ToArray())
            if (setting.CourseModuleId is > 0)
                result.TryAdd(setting.CourseModuleId.Value.ToString(CultureInfo.InvariantCulture), setting);
        return result;
    }

    private static AssignmentSettingsSummary EnrichOfficial(AssignmentSettingsSummary settings, JsonElement assignment)
    {
        var introFiles = ReadIntroAttachments(assignment, out var introFilesComplete, "introfiles");
        var open = ReadOfficialDate(assignment, "allowsubmissionsfromdate");
        var due = ReadOfficialDate(assignment, "duedate");
        var cutoff = ReadOfficialDate(assignment, "cutoffdate");
        var cmid = ReadId(assignment, "cmid");
        return settings with
        {
            CourseModuleId = cmid > 0 ? cmid : null,
            NoSubmissions = ReadOptionalBoolean(assignment, "nosubmissions"),
            DescriptionPresent = assignment.TryGetProperty("intro", out var intro) && intro.ValueKind == JsonValueKind.String,
            IntroFiles = introFiles, IntroFilesComplete = introFilesComplete,
            OpenAt = open.Date, OpenAtKnown = open.Known,
            DueAt = due.Date, DueAtKnown = due.Known,
            CutoffAt = cutoff.Date, CutoffAtKnown = cutoff.Known
        };
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number) && number is 0 or 1 => number == 1,
            _ => null
        };
    }

    private static (DateTimeOffset? Date, bool Known) ReadOfficialDate(JsonElement assignment, string name)
    {
        var timestamp = ReadNullableLong(assignment, name);
        if (timestamp is null or < 0 || timestamp > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return (null, false);
        return (timestamp == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(timestamp.Value), true);
    }

    private static long ReadId(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return -1;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => -1
        };
    }

    private static IReadOnlyList<CourseModuleFile>? ReadIntroAttachments(
        JsonElement assignment,
        out bool? isComplete,
        string propertyName = "introattachments")
    {
        isComplete = null;
        if (assignment.ValueKind != JsonValueKind.Object ||
            !assignment.TryGetProperty(propertyName, out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var projected = files.EnumerateArray()
            .Where(file => file.ValueKind == JsonValueKind.Object)
            .Select(file => new CourseModuleFile(
                Type: ReadString(file, "type"),
                FileName: ReadString(file, "filename"),
                FilePath: ReadString(file, "filepath"),
                FileSize: ReadNullableLong(file, "filesize"),
                MimeType: ReadString(file, "mimetype"),
                FileUrl: ReadString(file, "fileurl"),
                IsExternalFile: ReadNullableBoolean(file, "isexternalfile")))
            .Where(file => !string.IsNullOrWhiteSpace(file.FileUrl))
            .ToArray();
        isComplete = projected.Length == files.GetArrayLength();
        return projected;
    }

    private static long? ResolveScaleId(decimal grade)
    {
        var absolute = Math.Abs(grade);
        return grade < 0 && absolute <= long.MaxValue && decimal.Truncate(absolute) == absolute
            ? (long)absolute
            : null;
    }

    private static long? ReadNullableLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool? ReadNullableBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => null
        };
    }

    private static bool TryReadDecimal(JsonElement value, out decimal result)
    {
        result = 0m;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            result = number;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ParseMoodleId(string value, string parameterName)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            return id;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico do Moodle.", parameterName);
    }
}
