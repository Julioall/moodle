using System.Text.Json;
using Moodle.Application.MoodleApi;

namespace Moodle.Infrastructure.MoodleApi;

internal static class MoodleResponseParser
{
    public static JsonElement Parse(string payload, bool allowEmptyResponse = false)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            if (allowEmptyResponse)
            {
                using var emptyDocument = JsonDocument.Parse("null");
                return emptyDocument.RootElement.Clone();
            }

            throw new MoodleApiException(
                MoodleErrorContract.InvalidResponse,
                "Moodle returned an empty response.");
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("exception", out var exceptionElement))
            {
                var hasErrorCode = root.TryGetProperty("errorcode", out var errorCodeElement);
                var errorCode = ReadBoundedString(errorCodeElement, 128);
                var exceptionType = ReadBoundedString(exceptionElement, 192);
                throw new MoodleApiException(
                    string.IsNullOrWhiteSpace(errorCode) ? MoodleErrorContract.ApiError : errorCode,
                    "Moodle returned a structured Web Service error.",
                    remoteErrorCode: errorCode,
                    remoteExceptionType: exceptionType,
                    remoteErrorCodePresent: hasErrorCode,
                    remoteExceptionTypePresent: true);
            }

            return root.Clone();
        }
        catch (JsonException ex)
        {
            throw new MoodleApiException(
                MoodleErrorContract.InvalidResponse,
                "Moodle returned invalid JSON.",
                innerException: ex);
        }
    }

    private static string? ReadBoundedString(JsonElement value, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return text is not null && text.Length <= maximumLength ? text : null;
    }
}
