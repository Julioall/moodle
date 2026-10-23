using System.Text.Json;
using System.Text.Json.Nodes;
using Moodle.Domain;

namespace Moodle.Application.Auditing;

public static class AuditPayloadSanitizer
{
    private const string RedactedValue = "[REDACTED]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] SensitiveNameFragments =
    [
        "password",
        "passwd",
        "pwd",
        "token",
        "secret",
        "authorization",
        "cookie",
        "connectionstring",
        "apikey",
        "api_key",
        "wstoken",
        "sesskey",
        "privatekey",
        "privateaccesskey",
        "userprivateaccesskey",
        "accesskey",
        "session",
        "sessionid",
        "sessionidentifier",
        "refresh",
        "clientsecret",
        "jwt",
        "bearer"
    ];

    public static string SerializeSanitized(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value, JsonOptions);
        var sanitized = SanitizeElement(element, propertyName: null);
        return JsonSerializer.Serialize(sanitized, JsonOptions);
    }

    public static string SanitizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var sanitized = SanitizeElement(document.RootElement, propertyName: null);
            return JsonSerializer.Serialize(sanitized, JsonOptions);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    public static JsonElement ToSanitizedElement(object? value)
    {
        using var document = JsonDocument.Parse(SerializeSanitized(value));
        return document.RootElement.Clone();
    }

    private static JsonNode? SanitizeElement(JsonElement element, string? propertyName)
    {
        if (IsSensitiveName(propertyName))
        {
            return JsonValue.Create(RedactedValue);
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => SanitizeObject(element),
            JsonValueKind.Array => SanitizeArray(element),
            JsonValueKind.String => JsonValue.Create(SanitizeString(element.GetString())),
            JsonValueKind.Number => JsonNode.Parse(element.GetRawText()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static JsonObject SanitizeObject(JsonElement element)
    {
        var sanitized = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            sanitized[property.Name] = SanitizeElement(property.Value, property.Name);
        }

        return sanitized;
    }

    private static JsonArray SanitizeArray(JsonElement element)
    {
        var sanitized = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            sanitized.Add(SanitizeElement(item, propertyName: null));
        }

        return sanitized;
    }

    private static string? SanitizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (LooksLikeAuthorizationValue(value) || LooksLikeJwt(value))
        {
            return RedactedValue;
        }

        return MoodleContentUrlSanitizer.Sanitize(value);
    }

    private static bool IsSensitiveName(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var normalized = propertyName
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return SensitiveNameFragments.Any(fragment =>
            normalized.Contains(
                fragment.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeAuthorizationValue(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeJwt(string value)
    {
        var trimmed = value.Trim();
        var parts = trimmed.Split('.');
        return parts.Length == 3 &&
               parts.All(part => part.Length > 10 && part.All(IsBase64UrlChar));
    }

    private static bool IsBase64UrlChar(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value is '-' or '_';
    }
}
