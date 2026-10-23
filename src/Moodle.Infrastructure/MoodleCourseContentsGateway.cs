using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Domain;

namespace Moodle.Infrastructure;

internal sealed partial class MoodleCourseContentsGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient) : IMoodleCourseContentsGateway
{
    private readonly MoodleApiOptions _options = options.Value;

    public async Task<CourseContentsSummary> GetCourseContentsAsync(
        string userExternalId,
        string courseId,
        IReadOnlyCollection<string> moduleTypes,
        bool includeHidden,
        bool onlyWithFiles,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para fluxos reais. Ajuste a configuracao para usar Moodle real.");
        }

        var normalizedCourseId = ParseMoodleId(courseId, "courseId");
        var normalizedModuleTypes = NormalizeModuleTypes(moduleTypes);
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var payload = await restClient.CallAsync(
            credentials,
            "core_course_get_contents",
            BuildCourseContentsParameters(normalizedCourseId, includeHidden, normalizedModuleTypes)
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
            cancellationToken);

        var sectionsPayload = JsonSerializer.Deserialize<List<SectionDto>>(payload.GetRawText()) ?? [];
        var sections = sectionsPayload
            .Select(section => ToSection(section, normalizedModuleTypes, includeHidden, onlyWithFiles))
            .ToArray();

        return new CourseContentsSummary(
            normalizedCourseId.ToString(CultureInfo.InvariantCulture),
            normalizedModuleTypes.ToArray(),
            includeHidden,
            onlyWithFiles,
            sections);
    }

    private static Dictionary<string, string> BuildCourseContentsParameters(
        int courseId,
        bool includeHidden,
        IReadOnlyCollection<string> moduleTypes)
    {
        var parameters = new Dictionary<string, string>
        {
            ["courseid"] = courseId.ToString(CultureInfo.InvariantCulture)
        };
        var options = new List<(string Name, string Value)>
        {
            ("excludemodules", "0"),
            ("excludecontents", "0")
        };

        if (includeHidden)
        {
            options.Add(("includestealthmodules", "1"));
        }

        if (moduleTypes.Count == 1)
        {
            options.Add(("modname", moduleTypes.Single()));
        }

        AddMoodleOptions(parameters, options);
        return parameters;
    }

    private static CourseSectionSummary ToSection(
        SectionDto dto,
        IReadOnlyCollection<string> moduleTypes,
        bool includeHidden,
        bool onlyWithFiles)
    {
        var modules = (dto.Modules ?? [])
            .Select(ToModule)
            .Select(module => onlyWithFiles ? MoodleLocalFileProjection.Project(module) : module)
            .Where(module => ShouldIncludeModule(module, moduleTypes, includeHidden, onlyWithFiles))
            .ToArray();
        var name = string.IsNullOrWhiteSpace(dto.Name)
            ? $"Secao {ToNullableInt(dto.SectionNumber) ?? 0}"
            : dto.Name;

        return new CourseSectionSummary(
            ToIdString(dto.Id),
            ToNullableInt(dto.SectionNumber),
            name,
            ToPlainText(dto.Summary),
            ToBool(dto.Visible),
            modules.Length,
            modules.Length == 0,
            modules);
    }

    private static CourseModuleSummary ToModule(ModuleDto dto)
    {
        return new CourseModuleSummary(
            ToIdString(dto.Id),
            ToOptionalIdString(dto.Instance),
            dto.ModuleType ?? string.Empty,
            dto.Name ?? string.Empty,
            MoodleContentUrlSanitizer.Sanitize(dto.Url),
            ToBool(dto.Visible),
            ToBool(dto.UserVisible),
            ToPlainText(dto.Description),
            ToPlainText(dto.AvailabilityInfo),
            (dto.Dates ?? [])
                .Select(ToModuleDate)
                .Where(date => date is not null)
                .Select(date => date!)
                .ToArray(),
            (dto.Contents ?? [])
                .Select(ToModuleFile)
                .ToArray());
    }

    private static CourseModuleDate? ToModuleDate(ModuleDateDto dto)
    {
        var seconds = ToInt64(dto.Timestamp);
        if (seconds is not > 0)
        {
            return null;
        }

        return new CourseModuleDate(
            string.IsNullOrWhiteSpace(dto.Label) ? "date" : dto.Label,
            DateTimeOffset.FromUnixTimeSeconds(seconds.Value));
    }

    private static CourseModuleFile ToModuleFile(ContentFileDto dto)
    {
        return new CourseModuleFile(
            string.IsNullOrWhiteSpace(dto.Type) ? null : dto.Type,
            string.IsNullOrWhiteSpace(dto.FileName) ? null : dto.FileName,
            string.IsNullOrWhiteSpace(dto.FilePath) ? null : dto.FilePath,
            ToInt64(dto.FileSize),
            string.IsNullOrWhiteSpace(dto.MimeType) ? null : dto.MimeType,
            MoodleContentUrlSanitizer.Sanitize(dto.FileUrl),
            ToBool(dto.IsExternalFile));
    }

    private static bool ShouldIncludeModule(
        CourseModuleSummary module,
        IReadOnlyCollection<string> moduleTypes,
        bool includeHidden,
        bool onlyWithFiles)
    {
        if (moduleTypes.Count > 0 && !moduleTypes.Contains(module.ModuleType, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!includeHidden && (module.Visible is false || module.UserVisible is false))
        {
            return false;
        }

        return !onlyWithFiles || module.Files.Count > 0;
    }

    private static IReadOnlyCollection<string> NormalizeModuleTypes(IReadOnlyCollection<string> moduleTypes)
    {
        return moduleTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddMoodleOptions(
        IDictionary<string, string> parameters,
        IReadOnlyList<(string Name, string Value)> options)
    {
        for (var i = 0; i < options.Count; i++)
        {
            parameters[$"options[{i}][name]"] = options[i].Name;
            parameters[$"options[{i}][value]"] = options[i].Value;
        }
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

    private static string? ToOptionalIdString(JsonElement value)
    {
        var id = ToIdString(value);
        return string.IsNullOrWhiteSpace(id) ? null : id;
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

    private static long? ToInt64(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
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

    private static string? ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutTags = HtmlTagRegex().Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalized = WhitespaceRegex().Replace(decoded, " ").Trim();

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    private sealed class SectionDto
    {
        [JsonPropertyName("id")]
        public JsonElement Id { get; init; }

        [JsonPropertyName("section")]
        public JsonElement SectionNumber { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("visible")]
        public JsonElement Visible { get; init; }

        [JsonPropertyName("modules")]
        public IReadOnlyList<ModuleDto>? Modules { get; init; }
    }

    private sealed class ModuleDto
    {
        [JsonPropertyName("id")]
        public JsonElement Id { get; init; }

        [JsonPropertyName("instance")]
        public JsonElement Instance { get; init; }

        [JsonPropertyName("modname")]
        public string? ModuleType { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("visible")]
        public JsonElement Visible { get; init; }

        [JsonPropertyName("uservisible")]
        public JsonElement UserVisible { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("availabilityinfo")]
        public string? AvailabilityInfo { get; init; }

        [JsonPropertyName("dates")]
        public IReadOnlyList<ModuleDateDto>? Dates { get; init; }

        [JsonPropertyName("contents")]
        public IReadOnlyList<ContentFileDto>? Contents { get; init; }
    }

    private sealed class ModuleDateDto
    {
        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("timestamp")]
        public JsonElement Timestamp { get; init; }
    }

    private sealed class ContentFileDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("filename")]
        public string? FileName { get; init; }

        [JsonPropertyName("filepath")]
        public string? FilePath { get; init; }

        [JsonPropertyName("filesize")]
        public JsonElement FileSize { get; init; }

        [JsonPropertyName("mimetype")]
        public string? MimeType { get; init; }

        [JsonPropertyName("fileurl")]
        public string? FileUrl { get; init; }

        [JsonPropertyName("isexternalfile")]
        public JsonElement IsExternalFile { get; init; }
    }
}
