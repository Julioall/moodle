using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Domain;

namespace Moodle.Infrastructure;

internal sealed partial class MoodleForumGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient,
    IMoodleFunctionCatalog functionCatalog) : IMoodleForumGateway
{
    private const string AddDiscussionFunction = "mod_forum_add_discussion";
    private const string AddDiscussionPostFunction = "mod_forum_add_discussion_post";
    private const string LegacyDiscussionFunction = "mod_forum_get_forum_discussions";
    private const string PaginatedDiscussionFunction = "mod_forum_get_forum_discussions_paginated";
    private const string HtmlMessageFormat = "1";
    private const int LegacyLastPostDescSortOrder = 1;
    private const int LegacyLastPostAscSortOrder = 2;
    private const int LegacyCreatedDescSortOrder = 3;
    private const int LegacyCreatedAscSortOrder = 4;
    private static readonly string[] DiscussionSortFields = ["id", "timemodified", "timestart", "timeend"];
    private static readonly string[] PostSortFields = ["id", "created", "modified"];
    private readonly MoodleApiOptions _options = options.Value;

    public async Task<IReadOnlyList<ForumDiscussionSummary>> GetForumDiscussionsPaginatedAsync(
        string userExternalId,
        string forumId,
        string sortBy,
        string sortDirection,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para fluxos reais. Ajuste a configuracao para usar Moodle real.");
        }

        var normalizedForumId = ParseMoodleId(forumId, "forumId");
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var profile = await functionCatalog.GetCurrentAsync(false, cancellationToken);
        var discussionFunction = ResolveDiscussionFunction(profile);
        var pageNumber = Math.Max(0, page - 1);
        var normalizedPageSize = Math.Max(1, pageSize);
        var parameters = discussionFunction == LegacyDiscussionFunction
            ? new Dictionary<string, object?>
            {
                ["forumid"] = normalizedForumId.ToString(CultureInfo.InvariantCulture),
                ["sortorder"] = ResolveLegacySortOrder(sortBy, sortDirection),
                ["page"] = pageNumber,
                ["perpage"] = normalizedPageSize
            }
            : new Dictionary<string, object?>
            {
                ["forumid"] = normalizedForumId.ToString(CultureInfo.InvariantCulture),
                ["sortby"] = NormalizeSortField(sortBy, DiscussionSortFields, "timemodified"),
                ["sortdirection"] = NormalizeSortDirection(sortDirection),
                ["page"] = pageNumber,
                ["perpage"] = normalizedPageSize
            };
        var payload = await restClient.CallAsync(
            credentials,
            discussionFunction,
            parameters,
            cancellationToken);

        return GetDiscussionElements(payload)
            .Select(ToDiscussion)
            .Where(discussion => !string.IsNullOrWhiteSpace(discussion.DiscussionId))
            .ToArray();
    }

    private static string ResolveDiscussionFunction(MoodleFunctionProfile profile)
    {
        var availableFunctions = profile.Functions
            .Where(function => function.IsAvailable)
            .Select(function => function.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Moodle 5.x and the FIEG/SENAI services expose the non-paginated function,
        // which already supports page/perpage. Keep the legacy endpoint as
        // the preferred compatible route and use the paginated extension only
        // for connections that advertise it instead.
        if (availableFunctions.Contains(LegacyDiscussionFunction))
        {
            return LegacyDiscussionFunction;
        }

        if (availableFunctions.Contains(PaginatedDiscussionFunction))
        {
            return PaginatedDiscussionFunction;
        }

        throw new MoodleApiException(
            MoodleErrorContract.FunctionNotAllowed,
            "A conexão Moodle não oferece uma função compatível para listar discussões de fórum.",
            functionName: LegacyDiscussionFunction);
    }

    private static int ResolveLegacySortOrder(string sortBy, string sortDirection)
    {
        var ascending = string.Equals(sortDirection?.Trim(), "ASC", StringComparison.OrdinalIgnoreCase);
        var normalizedSortBy = string.IsNullOrWhiteSpace(sortBy)
            ? "timemodified"
            : sortBy.Trim().ToLowerInvariant();

        // The legacy Moodle API exposes a numeric sort order rather than the
        // sort field/direction pair accepted by the paginated extension.
        // timemodified maps to last post; the remaining supported UI fields
        // are closest to discussion creation order on that API.
        return normalizedSortBy == "timemodified"
            ? ascending ? LegacyLastPostAscSortOrder : LegacyLastPostDescSortOrder
            : ascending ? LegacyCreatedAscSortOrder : LegacyCreatedDescSortOrder;
    }

    private static IReadOnlyList<JsonElement> GetDiscussionElements(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("discussions", out var discussions) &&
            discussions.ValueKind == JsonValueKind.Array)
        {
            return discussions.EnumerateArray().ToArray();
        }

        // A few older Moodle proxies return the list directly instead of the
        // documented { discussions, warnings } envelope.
        return payload.ValueKind == JsonValueKind.Array
            ? payload.EnumerateArray().ToArray()
            : [];
    }

    public async Task<IReadOnlyList<ForumPostSummary>> GetDiscussionPostsAsync(
        string userExternalId,
        string discussionId,
        string sortBy,
        string sortDirection,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para fluxos reais. Ajuste a configuracao para usar Moodle real.");
        }

        var normalizedDiscussionId = ParseMoodleId(discussionId, "discussionId");
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var payload = await restClient.CallAsync(
            credentials,
            "mod_forum_get_discussion_posts",
            new Dictionary<string, string>
            {
                ["discussionid"] = normalizedDiscussionId.ToString(CultureInfo.InvariantCulture),
                ["sortby"] = NormalizeSortField(sortBy, PostSortFields, "created"),
                ["sortdirection"] = NormalizeSortDirection(sortDirection)
            }.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
            cancellationToken);

        var posts = JsonSerializer.Deserialize<DiscussionPostsResponseDto>(payload.GetRawText());
        return (posts?.Posts ?? [])
            .Select(ToPost)
            .Where(post => !string.IsNullOrWhiteSpace(post.PostId))
            .ToArray();
    }

    public async Task<ForumWriteResult> AddDiscussionAsync(
        string userExternalId,
        string forumId,
        string subject,
        string messageHtml,
        int groupId,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para escritas Moodle reais.");
        }

        ValidateWriteInput(userExternalId, subject, messageHtml);
        var normalizedForumId = ParseMoodleId(forumId, "forumId");
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        EnsureCanWrite(credentials);

        var payload = await restClient.CallWriteAsync(credentials, AddDiscussionFunction, new Dictionary<string, object?>
        {
            ["forumid"] = normalizedForumId.ToString(CultureInfo.InvariantCulture),
            ["subject"] = subject.Trim(),
            ["message"] = messageHtml.Trim(),
            ["groupid"] = groupId.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken);
        var root = ParseMoodleSuccessPayload(payload.GetRawText(), "O Moodle rejeitou a criacao de discussao no forum");
        return new ForumWriteResult(
            Success: true,
            AddDiscussionFunction,
            MoodleStatus: "ok",
            DiscussionId: root is null ? null : GetOptionalIdString(root.Value, "discussionid"),
            PostId: null,
            Warnings: root is null ? [] : GetWarningMessages(root.Value));
    }

    public async Task<ForumWriteResult> AddDiscussionPostAsync(
        string userExternalId,
        string postId,
        string subject,
        string messageHtml,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para escritas Moodle reais.");
        }

        ValidateWriteInput(userExternalId, subject, messageHtml);
        var normalizedPostId = ParseMoodleId(postId, "postId");
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        EnsureCanWrite(credentials);

        var payload = await restClient.CallWriteAsync(credentials, AddDiscussionPostFunction, new Dictionary<string, object?>
        {
            ["postid"] = normalizedPostId.ToString(CultureInfo.InvariantCulture),
            ["subject"] = subject.Trim(),
            ["message"] = messageHtml.Trim(),
            ["messageformat"] = HtmlMessageFormat
        }, cancellationToken);
        var root = ParseMoodleSuccessPayload(payload.GetRawText(), "O Moodle rejeitou a resposta no forum");
        return new ForumWriteResult(
            Success: true,
            AddDiscussionPostFunction,
            MoodleStatus: "ok",
            DiscussionId: null,
            PostId: root is null ? null : GetOptionalIdString(root.Value, "postid"),
            Warnings: root is null ? [] : GetWarningMessages(root.Value));
    }

    public async Task<IReadOnlyList<ForumInfo>> GetForumsByCoursesAsync(
        string userExternalId,
        string courseId,
        CancellationToken cancellationToken)
    {
        if (_options.UseStubData)
        {
            throw new InvalidOperationException("UseStubData esta desativado para fluxos reais. Ajuste a configuracao para usar Moodle real.");
        }

        var normalizedCourseId = ParseMoodleId(courseId, "courseId");
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var payload = await restClient.CallAsync(
            credentials,
            "mod_forum_get_forums_by_courses",
            new Dictionary<string, string>
            {
                ["courseids[0]"] = normalizedCourseId.ToString(CultureInfo.InvariantCulture)
            }.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
            cancellationToken);

        var forums = JsonSerializer.Deserialize<IReadOnlyList<JsonElement>>(payload.GetRawText()) ?? [];
        return forums
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => new ForumInfo(
                GetIdString(element, "id"),
                GetIdString(element, "course"),
                GetString(element, "type"),
                GetString(element, "name"),
                GetNullableInt(element, "numdiscussions")))
            .Where(info => !string.IsNullOrWhiteSpace(info.ForumId))
            .ToArray();
    }

    private static ForumDiscussionSummary ToDiscussion(JsonElement dto)
    {
        var discussionId = GetIdString(dto, "discussion");
        var firstPostId = GetIdString(dto, "id");
        if (string.IsNullOrWhiteSpace(discussionId))
        {
            discussionId = firstPostId;
        }

        return new ForumDiscussionSummary(
            discussionId,
            firstPostId,
            GetString(dto, "name") ?? GetString(dto, "subject") ?? string.Empty,
            GetString(dto, "subject") ?? GetString(dto, "name") ?? string.Empty,
            ToPlainText(GetString(dto, "message")),
            GetOptionalIdString(dto, "userid"),
            GetString(dto, "userfullname") ?? GetString(dto, "usermodifiedfullname"),
            ToDateTimeOffset(dto, "created") ?? ToDateTimeOffset(dto, "timecreated"),
            ToDateTimeOffset(dto, "modified") ?? ToDateTimeOffset(dto, "timemodified"),
            ToDateTimeOffset(dto, "timemodified"),
            GetNullableInt(dto, "numreplies") ?? GetNullableInt(dto, "replies") ?? 0,
            GetNullableInt(dto, "numunread") ?? 0,
            GetBool(dto, "pinned"),
            GetBool(dto, "locked"),
            GetBool(dto, "canreply"),
            PostsReturned: 0,
            PostsTotal: 0,
            Posts: []);
    }

    private static ForumPostSummary ToPost(JsonElement dto)
    {
        var discussionId = GetIdString(dto, "discussionid");
        if (string.IsNullOrWhiteSpace(discussionId))
        {
            discussionId = GetIdString(dto, "discussion");
        }

        return new ForumPostSummary(
            GetIdString(dto, "id"),
            discussionId,
            GetOptionalIdString(dto, "parentid") ?? GetOptionalIdString(dto, "parent"),
            GetOptionalIdString(dto, "userid") ?? GetNestedOptionalIdString(dto, "author", "id"),
            GetString(dto, "userfullname") ?? GetNestedString(dto, "author", "fullname"),
            GetString(dto, "subject") ?? GetString(dto, "replysubject") ?? string.Empty,
            ToPlainText(GetString(dto, "message")),
            ToDateTimeOffset(dto, "created") ?? ToDateTimeOffset(dto, "timecreated"),
            ToDateTimeOffset(dto, "modified") ?? ToDateTimeOffset(dto, "timemodified"),
            GetBool(dto, "deleted"),
            GetBool(dto, "canreply"),
            GetBool(dto, "postread") ?? GetBool(dto, "read"),
            GetBool(dto, "isprivatereply"),
            GetChildren(dto),
            GetAttachments(dto));
    }

    private static IReadOnlyList<string> GetChildren(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("children", out var children) ||
            children.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return children
            .EnumerateArray()
            .Select(ToIdString)
            .Where(child => !string.IsNullOrWhiteSpace(child))
            .ToArray();
    }

    private static IReadOnlyList<ForumAttachmentSummary> GetAttachments(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("attachments", out var attachments) ||
            attachments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return attachments
            .EnumerateArray()
            .Where(attachment => attachment.ValueKind == JsonValueKind.Object)
            .Select(attachment => new ForumAttachmentSummary(
                GetString(attachment, "filename"),
                GetString(attachment, "filepath"),
                GetString(attachment, "mimetype"),
                GetNullableLong(attachment, "filesize"),
                MoodleContentUrlSanitizer.Sanitize(GetString(attachment, "fileurl")),
                GetBool(attachment, "isexternalfile")))
            .ToArray();
    }


    private static int ParseMoodleId(string value, string parameterName)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            return id;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico do Moodle.", parameterName);
    }

    private static string NormalizeSortField(string sortBy, IReadOnlyCollection<string> allowedFields, string defaultField)
    {
        var normalized = string.IsNullOrWhiteSpace(sortBy)
            ? defaultField
            : sortBy.Trim().ToLowerInvariant();

        return allowedFields.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : defaultField;
    }

    private static string NormalizeSortDirection(string sortDirection)
    {
        return string.Equals(sortDirection?.Trim(), "ASC", StringComparison.OrdinalIgnoreCase)
            ? "ASC"
            : "DESC";
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

    private static string GetIdString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property)
                ? ToIdString(property)
                : string.Empty;
    }

    private static string? GetOptionalIdString(JsonElement element, string propertyName)
    {
        var id = GetIdString(element, propertyName);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static string? GetNestedOptionalIdString(JsonElement element, string objectName, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(objectName, out var nested) ||
            nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetIdString(nested, propertyName);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.ToString(),
            _ => null
        };
    }

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(objectName, out var nested) ||
            nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(nested, propertyName);
    }

    private static IReadOnlyList<string> GetWarningMessages(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("warnings", out var warnings) ||
            warnings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return warnings
            .EnumerateArray()
            .Select(ToWarningMessage)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .ToArray();
    }

    private static string ToWarningMessage(JsonElement warning)
    {
        if (warning.ValueKind != JsonValueKind.Object)
        {
            return warning.ToString();
        }

        var code = GetString(warning, "warningcode") ?? GetString(warning, "errorcode");
        var message = GetString(warning, "message");
        if (string.IsNullOrWhiteSpace(code))
        {
            return message ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(message)
            ? code
            : $"{code}: {message}";
    }

    private static int? GetNullableInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
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

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when property.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var boolean) => boolean,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number != 0,
            _ => null
        };
    }

    private static DateTimeOffset? ToDateTimeOffset(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var seconds = property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0
        };

        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    private static string? ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutScript = ScriptTagRegex().Replace(value, " ");
        var withoutStyle = StyleTagRegex().Replace(withoutScript, " ");
        var withoutTags = HtmlTagRegex().Replace(withoutStyle, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var normalized = WhitespaceRegex().Replace(decoded, " ").Trim();

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static void ValidateWriteInput(string userExternalId, string subject, string messageHtml)
    {
        if (string.IsNullOrWhiteSpace(userExternalId))
        {
            throw new ArgumentException("O usuario Moodle e obrigatorio.", nameof(userExternalId));
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("O assunto do post e obrigatorio.", nameof(subject));
        }

        if (string.IsNullOrWhiteSpace(messageHtml))
        {
            throw new ArgumentException("A mensagem do post e obrigatoria.", nameof(messageHtml));
        }
    }

    private static void EnsureCanWrite(MoodleCredentials credentials)
    {
        if (!credentials.CanWrite)
        {
            throw new InvalidOperationException("A conexao Moodle atual nao permite escrita.");
        }
    }

    private static JsonElement? ParseMoodleSuccessPayload(string payload, string errorPrefix)
    {
        if (string.IsNullOrWhiteSpace(payload) ||
            string.Equals(payload.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        if (!root.TryGetProperty("exception", out var exceptionElement))
        {
            return root;
        }

        var errorCode = root.TryGetProperty("errorcode", out var errorCodeElement)
            ? errorCodeElement.GetString()
            : exceptionElement.GetString();
        throw new InvalidOperationException($"{errorPrefix}: {errorCode ?? "erro_desconhecido"}.");
    }

    [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex StyleTagRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    private sealed class DiscussionPostsResponseDto
    {
        [JsonPropertyName("posts")]
        public IReadOnlyList<JsonElement>? Posts { get; init; }
    }
}
