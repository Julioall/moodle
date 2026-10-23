using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Moodle.Application.Auditing;

namespace Moodle.Application.Tools;

/// <summary>Bounded public projection. Never modifies the original evidence or audit contract.</summary>
public static class MoodlePublicContentSanitizer
{
    public const string Withheld = "[CONTENT_WITHHELD: inspection_limit_or_unsupported]";
    private const string Redacted = "[REDACTED]";
    private const int MaxTextChars = 1_000_000;
    private const int MaxProjectionChars = 8_000_000;
    private const int MaxDecodeLayers = 3;
    private const string SecretName = @"(?:[\w.-]*(?:password|passwd|sesskey|token|secret|authorization|cookie|api[_-]?key|accesskey|privatekey|sessionid|sessionkey)[\w.-]*)";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Assignment = new(
        "(?<prefix>[\"']?" + SecretName + "[\"']?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|[^\\s&;,<>}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex Inputs = new("<input\\b(?:[^'\">]|\"[^\"]*\"|'[^']*')*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex SecretInput = new("\\b(?:name|id)\\s*=\\s*[\"']?" + SecretName + "(?:[\"'\\s>])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex InputValue = new("(?<prefix>\\bvalue\\s*=\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s>]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex Authorization = new(@"\b(?:Bearer|Basic)\s+[A-Za-z0-9+/_.=\-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    public static JsonElement ToSanitizedElement(object? value) => Inspect(value).Payload;

    public static MoodlePublicContentProjection Inspect(object? value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (json.Length > MaxProjectionChars)
            throw new InvalidOperationException("O conteúdo excede o limite de inspeção da resposta pública.");
        var node = JsonNode.Parse(AuditPayloadSanitizer.SanitizeJson(json));
        if (node is JsonValue root && root.TryGetValue<string>(out var text)) node = JsonValue.Create(SanitizeText(text));
        else Visit(node, 0);
        var payload = JsonSerializer.SerializeToElement(node);
        return new MoodlePublicContentProjection(payload, !payload.GetRawText().Contains(Withheld, StringComparison.Ordinal));
    }

    private static void Visit(JsonNode? node, int depth)
    {
        if (depth > 32)
            throw new InvalidOperationException("O conteúdo excede a profundidade de inspeção da resposta pública.");
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[key] = SanitizeText(text);
                else Visit(obj[key], depth + 1);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    array[i] = SanitizeText(text);
                else Visit(array[i], depth + 1);
            }
        }
    }

    public static string SanitizeText(string text)
    {
        if (text.Length > MaxTextChars) return Withheld;
        try
        {
            // Decode a bounded number of layers for escaped query strings/markup.
            var decoded = text;
            for (var i = 0; i < MaxDecodeLayers; i++)
            {
                var next = WebUtility.HtmlDecode(Uri.UnescapeDataString(decoded));
                if (string.Equals(next, decoded, StringComparison.Ordinal)) break;
                decoded = next;
            }
            if (!string.Equals(
                    WebUtility.HtmlDecode(Uri.UnescapeDataString(decoded)),
                    decoded,
                    StringComparison.Ordinal))
                return Withheld;
            var sanitized = Authorization.Replace(decoded, Redacted);
            sanitized = Inputs.Replace(sanitized, match => SecretInput.IsMatch(match.Value)
                ? InputValue.Replace(match.Value, "${prefix}\"" + Redacted + "\"") : match.Value);
            sanitized = Assignment.Replace(sanitized, "${prefix}\"" + Redacted + "\"");
            // Preserve original encoding when no redaction was needed.
            return sanitized == decoded ? text : sanitized;
        }
        catch (Exception error) when (error is RegexMatchTimeoutException or UriFormatException)
        {
            return Withheld;
        }
    }
}

public sealed record MoodlePublicContentProjection(JsonElement Payload, bool Complete);
