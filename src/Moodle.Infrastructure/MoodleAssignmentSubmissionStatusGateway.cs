using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moodle.Application.Abstractions;
using Moodle.Application.Grading;

namespace Moodle.Infrastructure;

internal sealed class MoodleAssignmentSubmissionStatusGateway(
    IOptions<MoodleApiOptions> options,
    IMoodleCredentialsProvider credentialsProvider,
    IMoodleRestClient restClient,
    IMemoryCache memoryCache) : IMoodleAssignmentSubmissionStatusGateway
{
    private const string MoodleFunction = "mod_assign_get_submission_status";
    private static readonly Regex HtmlTagRegex = new("<[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly MoodleApiOptions _options = options.Value;

    public async Task<AssignmentSubmissionAttemptStatus?> GetSubmissionStatusAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        CancellationToken cancellationToken)
        => await GetSubmissionStatusCoreAsync(userExternalId, assignmentId, studentId, bypassCache: false, cancellationToken);

    public async Task<AssignmentSubmissionAttemptStatus?> GetSubmissionStatusLiveAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        CancellationToken cancellationToken)
        => await GetSubmissionStatusCoreAsync(userExternalId, assignmentId, studentId, bypassCache: true, cancellationToken);

    private async Task<AssignmentSubmissionAttemptStatus?> GetSubmissionStatusCoreAsync(
        string userExternalId,
        string assignmentId,
        string studentId,
        bool bypassCache,
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

        var assignmentIdNumber = ParseMoodleId(assignmentId, nameof(assignmentId));
        var studentIdNumber = ParseMoodleId(studentId, nameof(studentId));
        var credentials = await credentialsProvider.GetCurrentCredentialsAsync(cancellationToken);
        var cacheKey = $"moodle_assign_submission_status:{credentials.ConnectionId}:{assignmentIdNumber}:{studentIdNumber}";
        if (!bypassCache && memoryCache.TryGetValue(cacheKey, out SubmissionStatusCacheEntry? cached) && cached is not null)
        {
            return cached.Status;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["assignid"] = assignmentIdNumber.ToString(CultureInfo.InvariantCulture),
            ["userid"] = studentIdNumber.ToString(CultureInfo.InvariantCulture)
        };
        var payload = bypassCache
            ? await restClient.CallReadOnceAsync(credentials, MoodleFunction, parameters, cancellationToken)
            : await restClient.CallAsync(credentials, MoodleFunction, parameters, cancellationToken);

        var status = ParseStatus(payload.GetRawText(), assignmentIdNumber, studentIdNumber);
        if (!bypassCache)
        {
            memoryCache.Set(cacheKey, new SubmissionStatusCacheEntry(status), TimeSpan.FromMinutes(5));
        }
        return status;
    }

    internal static AssignmentSubmissionAttemptStatus? ParseStatus(
        string payload,
        long assignmentId,
        long studentId)
    {
        if (string.IsNullOrWhiteSpace(payload) ||
            string.Equals(payload.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var lastAttempt = root.TryGetProperty("lastattempt", out var lastAttemptElement) &&
            lastAttemptElement.ValueKind == JsonValueKind.Object
                ? lastAttemptElement
                : default;
        var submission = lastAttempt.ValueKind == JsonValueKind.Object &&
            lastAttempt.TryGetProperty("submission", out var submissionElement) &&
            submissionElement.ValueKind == JsonValueKind.Object
                ? submissionElement
                : default;

        var attemptNumber = ReadNullableIntProperty(submission, "attemptnumber") ??
            ReadNullableIntProperty(lastAttempt, "attemptnumber") ??
            ReadNullableIntProperty(root, "attemptnumber");
        var submissionStatus = ReadStringProperty(submission, "status") ??
            ReadStringProperty(lastAttempt, "submissionstatus") ??
            ReadStringProperty(root, "status");

        return new AssignmentSubmissionAttemptStatus(
            assignmentId.ToString(CultureInfo.InvariantCulture),
            studentId.ToString(CultureInfo.InvariantCulture),
            attemptNumber,
            string.IsNullOrWhiteSpace(submissionStatus) ? null : submissionStatus,
            HasFeedback: HasExistingFeedback(root));
    }

    private static int? ReadNullableIntProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    internal static bool HasExistingFeedback(JsonElement root)
    {
        // Moodle returns feedback plugin payloads under feedback.feedbackdata.
        // Keep the direct/nested alternatives for older and customized sites.
        if (HasFeedbackPlugins(root, "feedbackplugins") ||
            HasNestedFeedbackData(root, "feedback") ||
            HasNestedFeedbackPlugins(root, "feedback") ||
            (root.TryGetProperty("lastattempt", out var lastAttempt) &&
             lastAttempt.ValueKind == JsonValueKind.Object &&
             (HasNestedFeedbackData(lastAttempt, "feedback") ||
              HasFeedbackPlugins(lastAttempt, "feedbackplugins") ||
              HasNestedFeedbackPlugins(lastAttempt, "feedback"))))
        {
            return true;
        }

        return false;
    }

    private static bool HasNestedFeedbackData(JsonElement container, string propertyName)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(propertyName, out var feedback) ||
            feedback.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return HasFeedbackPlugins(feedback, "feedbackdata");
    }

    private static bool HasFeedbackPlugins(JsonElement container, string propertyName)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(propertyName, out var plugins) ||
            plugins.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return HasFeedbackPluginContent(plugins);
    }

    private static bool HasNestedFeedbackPlugins(JsonElement container, string propertyName)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(propertyName, out var feedback) ||
            feedback.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return HasFeedbackPlugins(feedback, "plugins");
    }

    private static bool HasFeedbackPluginContent(JsonElement plugins)
    {
        foreach (var plugin in plugins.EnumerateArray())
        {
            if (plugin.TryGetProperty("editorfields", out var editorFields) &&
                editorFields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in editorFields.EnumerateArray())
                {
                    if (field.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String &&
                        HasMeaningfulFeedbackText(text.GetString()))
                    {
                        return true;
                    }
                }
            }

            if (plugin.TryGetProperty("fileareas", out var fileAreas) &&
                fileAreas.ValueKind == JsonValueKind.Array &&
                fileAreas.EnumerateArray().Any(fileArea =>
                    fileArea.TryGetProperty("files", out var files) &&
                    files.ValueKind == JsonValueKind.Array &&
                    files.GetArrayLength() > 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMeaningfulFeedbackText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var decoded = WebUtility.HtmlDecode(value)
            .Replace('\u00A0', ' ');
        var withoutMarkup = HtmlTagRegex.Replace(decoded, string.Empty);
        return !string.IsNullOrWhiteSpace(withoutMarkup);
    }

    private static long ParseMoodleId(string value, string parameterName)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            return id;
        }

        throw new ArgumentException($"O parametro {parameterName} deve ser um identificador numerico do Moodle.", parameterName);
    }

    private sealed record SubmissionStatusCacheEntry(AssignmentSubmissionAttemptStatus? Status);

}
