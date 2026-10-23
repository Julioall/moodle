using System.Text;

namespace Moodle.Domain;

public static class MoodleContentUrlSanitizer
{
    private static readonly string[] SensitiveQueryNames =
    [
        "token",
        "wstoken",
        "sesskey",
        "privatekey",
        "accesskey",
        "secret"
    ];

    public static string? Sanitize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !IsHttpUrl(uri))
        {
            return trimmed;
        }

        var builder = new UriBuilder(uri)
        {
            Query = BuildSafeQuery(uri.Query)
        };

        return builder.Uri.ToString();
    }

    private static bool IsHttpUrl(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSafeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var safePairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IsSensitivePair(pair))
            .ToArray();

        if (safePairs.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < safePairs.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(safePairs[i]);
        }

        return builder.ToString();
    }

    private static bool IsSensitivePair(string pair)
    {
        var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
        var rawName = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
        var name = Uri.UnescapeDataString(rawName);

        return SensitiveQueryNames.Any(sensitive =>
            string.Equals(name, sensitive, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(sensitive, StringComparison.OrdinalIgnoreCase));
    }
}
