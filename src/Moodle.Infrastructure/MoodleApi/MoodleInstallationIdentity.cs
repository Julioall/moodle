using System.Security.Cryptography;
using System.Text;

namespace Moodle.Infrastructure.MoodleApi;

/// <summary>
/// Produces an opaque, process-independent identity for an installation
/// budget. Credentials, query strings, and fragments are never included.
/// </summary>
internal static class MoodleInstallationIdentity
{
    public static string Create(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri
            || (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A validated absolute Moodle HTTP endpoint is required.", nameof(baseUri));

        var scheme = baseUri.Scheme.ToLowerInvariant();
        var host = baseUri.IdnHost.ToLowerInvariant();
        var port = baseUri.IsDefaultPort ? string.Empty : $":{baseUri.Port}";
        var path = baseUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).Trim('/');
        var canonical = string.IsNullOrEmpty(path)
            ? $"{scheme}://{host}{port}"
            : $"{scheme}://{host}{port}/{path}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest.AsSpan(0, 16));
    }
}
